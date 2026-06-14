# Text Extraction

Every document uploaded to Document AI passes through a text-extraction stage that converts the raw bytes into **Markdown**. The Markdown then drives the channel's internal pipeline — classification, Host field extraction, tenant field extraction (mechanism B), and title generation — and is the only text payload Document AI exposes to downstream consumers (RAG platforms, business systems, MCP clients) via REST / EventBus / MCP.

## Markdown-first contract

Document AI is an AI-native platform. Markdown is the **single text payload** of the pipeline. But what Markdown contributes depends on whether the source document has structure — be honest about both cases:

**With structure — real signal.** For contracts, reports, CSV, DOCX with headings, layout-aware OCR output (PP-StructureV3, Azure DI `prebuilt-layout`): headings, tables and lists are not formatting decoration — they are semantic signals that downstream RAG chunkers (header-path injection) and Document AI's own LLM prompts (system prompt: "input is Markdown") rely on. Use them in full.

**Without structure — container, not signal.** For OCR loose paragraphs, plain `.txt`, PP-OCRv4 line dumps, single-line notes: the Markdown wrapper is a **container name**, not a signal upgrade — `string.Join("\n\n", paragraphs)` and the plain text it wraps are byte-for-byte indistinguishable. We still route this through the Markdown contract so internal pipelines (classification / Host & tenant field extraction / title generation) and downstream consumers (RAG / business systems) stay on one shape. The wrapper buys uniformity, not LLM comprehension.

Contract obligations regardless of structure:

- Every text-extraction provider — built-in or third-party — **must** populate `TextExtractionResult.Markdown`. Plain-text fallbacks are a design violation.
- Even when the source has no structure, the provider must still emit flat Markdown paragraphs rather than expose a parallel raw-text channel — wrapping happens **inside the provider**, never bubbled up to the orchestrator.
- `Document.Markdown` is the **only** text field on the `Document` aggregate. Consumers that need plain text strip on demand via `Dignite.DocumentAI.Documents.MarkdownStripper.Strip(...)`; nothing is persisted in stripped form.

**Markdown-first is an engineering default, not a creed.** Out-of-band signals (coordinates, confidence, page metadata, form key-value structure, stamp positions) are **orthogonal** to Markdown. When future needs arise — citation highlighting on the source PDF, stamp localization, form key-value extraction, page-aware QA — they belong as **named, optional, strongly-typed** fields on `TextExtractionResult` (e.g. `IReadOnlyList<PageBlock>? PageBlocks`) or as a separate extractor interface orthogonal to `ITextExtractor`. **Forbidden**: stuffing such signals back into the Markdown string, or adding a `Dictionary<string, object>` extension slot. Each new out-of-band signal needs its own Issue — it's an architecture decision, not a quiet field addition.

Source contract: [`ITextExtractor`](../core/src/Dignite.DocumentAI.Abstractions/TextExtraction/ITextExtractor.cs), [`IMarkdownTextProvider`](../core/src/Dignite.DocumentAI.TextExtraction/IMarkdownTextProvider.cs).

## Two extraction paths

```
Upload → DocumentTextExtractionBackgroundJob
              │
              ├─→ digital text layer? (PDF / DOCX / HTML / TXT / CSV / RTF / EPUB …)
              │     └─→ IMarkdownTextProvider, dispatched per file by extension:
              │           • .pdf → PdfExtractor (PdfPig: text layer + embedded-image OCR, inlined)
              │           • .pptx / .docx → OpenXmlExtractor (structure + charts/tables + embedded-image OCR, inlined)
              │           • everything else → ElBruno MarkItDown (catch-all)
              │
              └─→ image / scan?
                    └─→ IOcrProvider (PaddleOCR / Azure Document Intelligence / Vision-LLM)

Both paths write the same shape: TextExtractionResult { Markdown, DetectedLanguage, NativePayload, ... }
                                  → Document.Markdown
```

The image/scan path is dispatched by file kind. The digital path is dispatched **per file by extension** across coexisting Markdown providers. Hosts wire one or more Markdown providers plus exactly one OCR provider via `[DependsOn(...)]`; switching providers is a host-level swap with no Application or Domain changes.

## Digital extraction — Markdown providers (dispatched by extension)

`IMarkdownTextProvider` implementations **coexist** and are dispatched **per file by extension** — unlike `IOcrProvider`, which is host-selected and mutually exclusive. Each provider self-declares the extensions it owns via `CanHandle(extension)` plus a `Priority`; `DefaultTextExtractor` picks the highest-priority provider that can handle the file, with ElBruno as the catch-all fallback. **Omitting a specialized provider module makes its extension fall back to ElBruno**, preserving prior behaviour.

| Provider | Owns | Notes |
|---|---|---|
| **PdfExtractor** (`Dignite.DocumentAI.TextExtraction.Pdf`, PdfPig) | `.pdf` | Extracts the digital text layer **and** embedded raster images. Each image is transcribed through the host-selected `IOcrProvider` and inlined into the Markdown at its reading position (#301), so embedded figures are no longer silently dropped. Vector-only graphics are an accepted blind spot (`GetImages()` does not see them). |
| **OpenXmlExtractor** (`Dignite.DocumentAI.TextExtraction.OpenXml`, OpenXML SDK) | `.pptx`, `.docx` | Owns the whole PowerPoint / Word pass: rebuilds structure (PPTX slide text + speaker notes, #307; DOCX headings / tables / lists / inline formatting / hyperlinks / text boxes, #308) **and** transcribes embedded raster images through the host-selected `IOcrProvider`, renders `ChartPart` backing data as Markdown tables, inlining everything at reading position. EMF/WMF vector images are an accepted blind spot. |
| **ElBruno MarkItDown** (`Dignite.DocumentAI.TextExtraction.ElBrunoMarkItDown`) | catch-all (HTML / TXT / CSV / RTF / EPUB; also `.docx` when the OpenXml module is absent, and `.pdf` when the Pdf module is absent) | Default fallback; enabled by the host module, no configuration. |

**Embedded images in digital PDFs (#301).** PdfExtractor reuses the host-selected `IOcrProvider` for figure transcription — no separate vision client is wired at the Markdown-provider layer, and the semantics are transcription only (no chart/describe modes). Image-heavy PDFs are bounded by `PdfExtractorOptions` (`MaxImagesPerPdf`, `MinImagePixels` — tiny decorative images are skipped). When images are dropped (cap reached / undecodable codec such as JBIG2/JPX) or a figure's OCR is truncated, the result is marked incomplete via the #268 completeness signal.

**Scanned / no-text-layer PDF.** If a PDF has no digital text layer, the Markdown provider returns empty Markdown — PdfExtractor does **not** OCR its images in that case — and the pipeline falls through to the whole-page `IOcrProvider` path. There is no double OCR.

**OpenXML PPTX / DOCX (#307 / #308).** OpenXmlExtractor owns the full PowerPoint / Word pass so the image↔text position linkage is preserved: it rebuilds the document structure itself and reuses the host-selected `IOcrProvider` for embedded-image transcription (no separate vision client, transcription only), renders charts and tables directly from the OpenXML (no OCR, no vector blind spot), and uses native alt-text (`docPr/@descr`) as the figure caption. DOCX collapses markup-compatibility (`mc:AlternateContent`) on open so a modern text box / picture is not read twice, and applies the accepted view of tracked changes (insertions kept, deletions dropped). Image caps live in `OpenXmlExtractorOptions`; dropped / undecodable images, truncated figure OCR, image-cap hits, unrenderable charts, and per-block parse failures trip the #268 completeness signal. Unlike PDF there is no whole-page OCR fallback, so once the module owns an extension an unopenable file reports empty + incomplete. **Required for `.pptx`** (ElBruno has no PresentationML converter); `.docx` degrades gracefully to ElBruno when the module is absent.

## OCR — choosing a provider

Document AI ships three OCR providers. Pick **one** in `host/src/DocumentAIHostModule.cs` based on the deployment scenario — `IOcrProvider` has a single registration, so the providers are mutually exclusive.

| | [PaddleOCR](ocr-paddleocr.md) (default) | [Azure Document Intelligence](ocr-azure-document-intelligence.md) | [Vision-LLM](ocr-vision-llm.md) |
|---|---|---|---|
| Where data goes | Local sidecar — never leaves the network | Cloud (Azure region) | Cloud (your LLM provider) |
| Setup | `docker compose up paddleocr` | Azure subscription + AI resource | Host LLM endpoint + a vision model id |
| Markdown output | Native (PP-StructureV3 / VL); flat (PP-OCRv4) | Native | Native (LLM transcribes to Markdown) |
| Cold start | ~30–60 s first run (model download ~600 MB) | Instant | Instant |
| Per-page cost | Free | F0 free tier / S0 ~$1.50 / 1000 pages | Paid per token (image + output) |
| Best at | Structured scans, CJK documents | Structured scans, forms | **Phone photos / thermal receipts** where layout OCR fails (#259); image-only PDFs |

> Cloud LLM OCR (Gemini / Mistral) and Google Document AI were evaluated and rejected — see issue #79 for the rationale (Japanese-language quality, region access, dependency footprint, free-tier shape). The [vision-LLM provider](ocr-vision-llm.md) (#259) is a later, vendor-agnostic `IChatClient`-based path for the photo/receipt scenario specifically.

Each provider's setup lives on its own page: **[PaddleOCR](ocr-paddleocr.md)** · **[Azure Document Intelligence](ocr-azure-document-intelligence.md)** · **[Vision-LLM](ocr-vision-llm.md)**.

### Provider-agnostic OCR pipeline behaviour

These apply regardless of which provider is wired:

- Document AI does not auto-switch OCR profiles per document. The OCR provider runs once with the host-configured model; there is no second OCR pass with a guessed specialized mode, and OCR average confidence is no longer a quality gate (#196).
- OCR completion always advances the document to classification. OCR average confidence was removed (#196) — it did not reliably predict real quality (skewed pages, blurry scans, layout issues are not reflected in average scores). A document reaches the review queue only via low classification confidence / no matching type, where the operator reclassifies, rejects (Document AI keeps the original file, Markdown, and rejection reason for audit, then marks the document failed — no "rerun OCR" or source-replacement path), or re-uploads a better source.
- Review state is two orthogonal axes (#284), not a durable audit ledger: a **disposition** axis `ReviewDisposition` (`NotReviewed` / `Confirmed` / `Rejected` — operator action only) and a **reason** axis `ReviewReasons` (`[Flags]`: `UnresolvedClassification` blocks Ready, `MissingRequiredFields` is non-blocking). "Needs operator attention" derives as `ReviewReasons != None && ReviewDisposition != Rejected`. Automatic re-classification may reset these; pipeline history remains available if a dedicated audit/event model is later needed.
- **Extraction completeness (#268).** A provider may report that it captured only part of a document (e.g. a vision-LLM output truncated at the token cap, or a dropped PDF page). This is carried on `TextExtractionResult.IsComplete` / `IncompleteReason`, archived into `Document.ExtractionMetadata`, and surfaced to downstream consumers on the REST `DocumentDto` as `ExtractionIsComplete` + `ExtractionIncompleteReason`. It is a **quality signal** (distinct from internal extraction provenance, which is not exposed); the channel does not gate Ready on it — consumers decide whether to accept, downgrade, or route to review. Providers that don't report it default to complete.

## Adding a custom OCR / digital provider

Implement `IOcrProvider` (for image/scan input) or `IMarkdownTextProvider` (for files with a digital text layer). Both contracts are documented in their source files; both demand Markdown output. A Markdown provider also declares which extensions it owns via `CanHandle(extension)` + `Priority` (use a non-negative priority for a specialized provider; the catch-all fallback sits at `MarkdownProviderPriorities.Fallback`).

The provider lives in its own module project (`Dignite.DocumentAI.Ocr.<Vendor>` or `Dignite.DocumentAI.TextExtraction.<Vendor>`) and is enabled by the host through `[DependsOn(...)]`. Markdown providers coexist (dispatched per file by extension), so adding one does not displace the others.

**Markdown-first responsibility is on the provider, not the orchestrator.** The `OcrResult` and `TextExtractionResult` types expose only a `Markdown` field — there is no parallel `RawText` channel. If the underlying OCR engine returns plain text only (e.g. PaddleOCR PP-OCRv4), the provider itself must wrap paragraphs into flat Markdown (typically `string.Join("\n\n", paragraphs)`). Returning empty Markdown when the engine produced text is a contract violation. Custom OCR providers should expose their model choice through provider/host configuration, not through Document AI core profile codes.

Custom OCR provider projects only need to reference `Dignite.DocumentAI.Ocr` — they do not need (and should not pull in) `Dignite.DocumentAI.TextExtraction` or `Dignite.DocumentAI.Abstractions`.

## See also

- OCR providers: [PaddleOCR](ocr-paddleocr.md) · [Azure Document Intelligence](ocr-azure-document-intelligence.md) · [Vision-LLM](ocr-vision-llm.md)
- [Classification pipeline](classification.md) — how the LLM consumes the Markdown
- [AI provider](ai-provider.md) — provider wiring for the keyed chat clients used by classification / field extraction / title generation
- [Deployment checklist](deployment-checklist.md) — verifying OCR after a sidecar upgrade

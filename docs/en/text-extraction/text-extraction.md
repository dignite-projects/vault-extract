# Text Extraction

Every document uploaded to Dignite Vault Extract passes through a text-extraction stage that converts the raw bytes into **Markdown**. The Markdown then drives the channel's internal pipeline — classification, Host field extraction, tenant field extraction (mechanism B), and title generation — and is the only text payload Dignite Vault Extract exposes to downstream consumers (RAG platforms, business systems, MCP clients) via REST / EventBus / MCP.

## Markdown-first contract

Dignite Vault Extract is an AI-native platform. Markdown is the **single text payload** of the pipeline. But what Markdown contributes depends on whether the source document has structure — be honest about both cases:

**With structure — real signal.** For contracts, reports, CSV/TSV, XLSX, DOCX/PPTX with headings and layout, or layout-aware OCR output (PP-StructureV3, Azure DI `prebuilt-layout`): headings, tables and lists are not formatting decoration — they are semantic signals that downstream RAG chunkers (header-path injection) and Dignite Vault Extract's own LLM prompts (system prompt: "input is Markdown") rely on. Use them in full.

**Without structure — container, not signal.** For OCR loose paragraphs, plain `.txt`, PP-OCRv4 line dumps, single-line notes: the Markdown wrapper is a **container name**, not a signal upgrade — `string.Join("\n\n", paragraphs)` and the plain text it wraps are byte-for-byte indistinguishable. We still route this through the Markdown contract so internal pipelines (classification / Host & tenant field extraction / title generation) and downstream consumers (RAG / business systems) stay on one shape. The wrapper buys uniformity, not LLM comprehension.

Contract obligations regardless of structure:

- Every text-extraction provider — built-in or third-party — **must** populate `TextExtractionResult.Markdown`. Plain-text fallbacks are a design violation.
- Even when the source has no structure, the provider must still emit flat Markdown paragraphs rather than expose a parallel raw-text channel — wrapping happens **inside the provider**, never bubbled up to the orchestrator.
- `Document.Markdown` is the **only** text field on the `Document` aggregate. Consumers that need plain text strip on demand via `Dignite.Vault.Extract.Documents.MarkdownStripper.Strip(...)`; nothing is persisted in stripped form.

**Markdown-first is an engineering default, not a creed.** Out-of-band signals (coordinates, confidence, page metadata, form key-value structure, stamp positions) are **orthogonal** to Markdown. When future needs arise — citation highlighting on the source PDF, stamp localization, form key-value extraction, page-aware QA — they belong as **named, optional, strongly-typed** fields on `TextExtractionResult` (e.g. `IReadOnlyList<PageBlock>? PageBlocks`) or as a separate extractor interface orthogonal to `ITextExtractor`. **Forbidden**: stuffing such signals back into the Markdown string, or adding a `Dictionary<string, object>` extension slot. Each new out-of-band signal needs its own Issue — it's an architecture decision, not a quiet field addition.

Source contract: [`ITextExtractor`](../core/src/Dignite.Vault.Extract.Abstractions/Parse/ITextExtractor.cs), [`IMarkdownTextProvider`](../core/src/Dignite.Vault.Extract.Parse/IMarkdownTextProvider.cs).

## Two extraction paths

```
Upload → DocumentParseBackgroundJob
              │
              ├─→ digital text layer? (PDF / DOCX / PPTX / XLSX / TXT / CSV / TSV …)
              │     └─→ IMarkdownTextProvider, dispatched per file by extension:
              │           • .pdf → PdfExtractor (PdfPig: text layer + embedded-image OCR, inlined)
              │           • .pptx / .docx → OpenXmlExtractor (structure + charts/tables + embedded-image OCR, inlined)
              │           • .xlsx → ElBruno MarkItDown Excel plugin
              │           • .csv / .tsv / .txt and everything else → ElBruno MarkItDown (catch-all)
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
| **PdfExtractor** (`Dignite.Vault.Extract.Parse.Pdf`, PdfPig) | `.pdf` | Extracts the digital text layer **and** embedded raster images. Each image is transcribed through the host-selected `IOcrProvider` and inlined into the Markdown at its reading position (#301), so embedded figures are no longer silently dropped. Vector-only graphics are an accepted blind spot (`GetImages()` does not see them). |
| **OpenXmlExtractor** (`Dignite.Vault.Extract.Parse.OpenXml`, OpenXML SDK) | `.pptx`, `.docx` | Owns the whole PowerPoint / Word pass: rebuilds structure (PPTX slide text + speaker notes, #307; DOCX headings / tables / lists / inline formatting / hyperlinks / text boxes, #308) **and** transcribes embedded raster images through the host-selected `IOcrProvider`, renders `ChartPart` backing data as Markdown tables, inlining everything at reading position. EMF/WMF vector images are an accepted blind spot. |
| **ElBruno MarkItDown** (`Dignite.Vault.Extract.Parse.ElBrunoMarkItDown`) | catch-all (XLSX via its Excel plugin; HTML / TXT / CSV / TSV / RTF / EPUB; also `.docx` when the OpenXml module is absent, and `.pdf` when the Pdf module is absent) | Default fallback; enabled by the host module. XLSX conversion emits each non-empty worksheet as Markdown tables. |

**XLSX resource boundary (#471).** An XLSX is a compressed ZIP package, so the 20 MiB upload limit alone does not bound the memory or CPU required to open it. Before ClosedXML materializes the workbook, the ElBruno provider drains every archive entry through a shared expanded-byte budget and rejects packages exceeding the entry, worksheet, or cell caps. The resulting Markdown has a separate character cap. A limit violation fails the parse run explicitly; it is never persisted as a successful empty extraction.

**Embedded images in digital PDFs (#301).** PdfExtractor reuses the host-selected `IOcrProvider` for figure transcription — no separate vision client is wired at the Markdown-provider layer, and the semantics are transcription only (no chart/describe modes). Image-heavy PDFs are bounded by `PdfExtractorOptions` (`MaxImagesPerPdf`, `MinImagePixels` — tiny decorative images are skipped). When images are dropped (cap reached / undecodable codec such as JBIG2/JPX) or a figure's OCR is truncated, the result is marked incomplete via the #268 completeness signal.

**Searchable / "sandwich" PDFs (#309).** A scan-to-searchable PDF stores, per page, a full-page scan raster plus an (often invisible) OCR text layer over it. PdfExtractor detects this and **skips the full-page raster** instead of re-OCRing it, which would duplicate the already-extracted text and burn a redundant vision call per page. The skip is intentional and non-lossy — the text layer is kept and it does **not** trip the #268 completeness signal (a deliberate skip is not a dropped figure). It is high-precision and errs toward keeping: it fires only when the image's **placement bbox** covers most of the page **and** the text layer reads as a whole-page transcription (vertically spread across the image region, not a thin edge caption band); a predominantly invisible `Tr 3` layer is the canonical confirmation. When unsure it keeps and transcribes the image, so a real full-page figure (figure + caption / infographic / photo) is never dropped. Tunable via `PdfExtractorOptions` (`SkipFullPageScanBackground`, on by default; `FullPageScanCoverageThreshold`, `FullPageScanMinTextLines`, `FullPageScanMinTextVerticalCoverage`, `FullPageScanMinInvisibleTextRatio`). Tiled multi-image scan pages and heavily margined/cropped scans are out of scope for now and still double-OCR.

**Scanned / no-text-layer PDF.** If a PDF has no digital text layer, the Markdown provider returns empty Markdown — PdfExtractor does **not** OCR its images in that case — and the pipeline falls through to the whole-page `IOcrProvider` path. There is no double OCR.

**OpenXML PPTX / DOCX (#307 / #308).** OpenXmlExtractor owns the full PowerPoint / Word pass so the image↔text position linkage is preserved: it rebuilds the document structure itself and reuses the host-selected `IOcrProvider` for embedded-image transcription (no separate vision client, transcription only), renders charts and tables directly from the OpenXML (no OCR, no vector blind spot), and uses native alt-text (`docPr/@descr`) as the figure caption. A figure (image or chart) inside a table cell is extracted as its own block after the table, since a Markdown cell cannot host a transcription. List nesting is indented three spaces per level so a child under an ordered (`1.`) item still nests in CommonMark. Block-level content controls (`w:sdt`) and custom-XML wrappers are recursed into (in body paragraphs and table cells alike), and legacy VML raster images (`w:pict`/`v:imagedata`) are transcribed alongside DrawingML ones, so neither is silently dropped; an unhandled block — or content-control nesting beyond a safety depth cap — that still carries text trips the completeness signal. Known limitation: VML images are not decorative-size-filtered (a VML shape's size lives in its style attribute, not `wp:extent`), so a tiny VML icon may be transcribed where its DrawingML equivalent would be skipped. DOCX collapses markup-compatibility (`mc:AlternateContent`) on open so a modern text box / picture is not read twice, and applies the accepted view of tracked changes (insertions kept, deletions dropped). Image caps live in `OpenXmlExtractorOptions`; dropped / undecodable images, truncated figure OCR, image-cap hits, unrenderable charts, and per-block parse failures trip the #268 completeness signal. Unlike PDF there is no whole-page OCR fallback, so once the module owns an extension an unopenable file reports empty + incomplete. **Required for `.pptx`** (ElBruno has no PresentationML converter); `.docx` degrades gracefully to ElBruno when the module is absent.

## OCR — choosing a provider

Dignite Vault Extract ships three OCR providers. Pick **one** in `host/src/ExtractHostModule.cs` based on the deployment scenario — `IOcrProvider` has a single registration, so the providers are mutually exclusive.

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

- Dignite Vault Extract does not auto-switch OCR profiles per document. The OCR provider runs once with the host-configured model; there is no second OCR pass with a guessed specialized mode, and OCR average confidence is no longer a quality gate (#196).
- OCR completion always advances the document to classification. OCR average confidence was removed (#196) — it did not reliably predict real quality (skewed pages, blurry scans, layout issues are not reflected in average scores). A document reaches the review queue only via low classification confidence / no matching type, where the operator reclassifies, rejects (Dignite Vault Extract keeps the original file, Markdown, and rejection reason for audit, then marks the document failed — no "rerun OCR" or source-replacement path), or re-uploads a better source.
- Review state is two orthogonal axes (#284), not a durable audit ledger: a **disposition** axis `ReviewDisposition` (`NotReviewed` / `Confirmed` / `Rejected` — operator action only) and a **reason** axis `ReviewReasons` (`[Flags]`: `UnresolvedClassification` blocks Ready, `MissingRequiredFields` is non-blocking). "Needs operator attention" derives as `ReviewReasons != None && ReviewDisposition != Rejected`. Automatic re-classification may reset these; pipeline history remains available if a dedicated audit/event model is later needed.
- **Extraction completeness (#268).** A provider may report that it captured only part of a document (e.g. a vision-LLM output truncated at the token cap, or a dropped PDF page). This is carried on `TextExtractionResult.IsComplete` / `IncompleteReason`, archived into `Document.ExtractionMetadata`, and surfaced to downstream consumers on the REST `DocumentDto` as `ExtractionIsComplete` + `ExtractionIncompleteReason`. It is a **quality signal** (distinct from internal extraction provenance, which is not exposed); the channel does not gate Ready on it — consumers decide whether to accept, downgrade, or route to review. Providers that don't report it default to complete.

## Adding a custom OCR / digital provider

Implement `IOcrProvider` (for image/scan input) or `IMarkdownTextProvider` (for files with a digital text layer). Both contracts are documented in their source files; both demand Markdown output. A Markdown provider also declares which extensions it owns via `CanHandle(extension)` + `Priority` (use a non-negative priority for a specialized provider; the catch-all fallback sits at `MarkdownProviderPriorities.Fallback`).

The provider lives in its own module project (`Dignite.Vault.Extract.Ocr.<Vendor>` or `Dignite.Vault.Extract.Parse.<Vendor>`) and is enabled by the host through `[DependsOn(...)]`. Markdown providers coexist (dispatched per file by extension), so adding one does not displace the others.

**Markdown-first responsibility is on the provider, not the orchestrator.** The `OcrResult` and `TextExtractionResult` types expose only a `Markdown` field — there is no parallel `RawText` channel. If the underlying OCR engine returns plain text only (e.g. PaddleOCR PP-OCRv4), the provider itself must wrap paragraphs into flat Markdown (typically `string.Join("\n\n", paragraphs)`). Returning empty Markdown when the engine produced text is a contract violation. Custom OCR providers should expose their model choice through provider/host configuration, not through Dignite Vault Extract core profile codes.

Custom OCR provider projects only need to reference `Dignite.Vault.Extract.Ocr` — they do not need (and should not pull in) `Dignite.Vault.Extract.Parse` or `Dignite.Vault.Extract.Abstractions`.

## See also

- OCR providers: [PaddleOCR](ocr-paddleocr.md) · [Azure Document Intelligence](ocr-azure-document-intelligence.md) · [Vision-LLM](ocr-vision-llm.md)
- [Classification pipeline](../pipeline/classification.md) — how the LLM consumes the Markdown
- [AI provider](../configuration/ai-provider.md) — provider wiring for the keyed chat clients used by classification / field extraction / title generation
- [Deployment checklist](../deployment/deployment-checklist.md) — verifying OCR after a sidecar upgrade

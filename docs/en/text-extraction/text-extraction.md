```json
//[doc-seo]
{
    "Description": "How Dignite Vault Extract turns uploads into Markdown: the Markdown-first contract, digital-document providers, OCR provider choice, extractor options and custom providers."
}
```

# Text Extraction

Every document uploaded to Dignite Vault Extract passes through a text-extraction stage that converts the raw bytes into **Markdown**. The Markdown then drives the channel's internal pipeline — classification, sub-document segmentation, field extraction and title generation — and is the only text payload Dignite Vault Extract exposes to downstream consumers (RAG platforms, business systems, MCP clients) through REST and MCP. The accepted file types and the upload size limit are listed in [Documents and Review](../documents/documents.md#uploading).

## Markdown-First Contract

Dignite Vault Extract is an AI-native platform. Markdown is the **single text payload** of the pipeline. But what Markdown contributes depends on whether the source document has structure — be honest about both cases:

**With structure — real signal.** For contracts, reports, CSV/TSV, XLSX, DOCX/PPTX with headings and layout, or layout-aware OCR output (PP-StructureV3, Azure DI `prebuilt-layout`): headings, tables and lists are not formatting decoration — they are semantic signals that downstream RAG chunkers (header-path injection) and Dignite Vault Extract's own LLM prompts (system prompt: "input is Markdown") rely on. Use them in full.

**Without structure — container, not signal.** For OCR loose paragraphs, plain `.txt`, PP-OCRv4 line dumps, single-line notes: the Markdown wrapper is a **container name**, not a signal upgrade — `string.Join("\n\n", paragraphs)` and the plain text it wraps are byte-for-byte indistinguishable. We still route this through the Markdown contract so internal pipelines (classification / field extraction / title generation) and downstream consumers (RAG / business systems) stay on one shape. The wrapper buys uniformity, not LLM comprehension.

Contract obligations regardless of structure:

- Every text-extraction provider — built-in or third-party — **must** populate `TextExtractionResult.Markdown`. Plain-text fallbacks are a design violation.
- Even when the source has no structure, the provider must still emit flat Markdown paragraphs rather than expose a parallel raw-text channel — wrapping happens **inside the provider**, never bubbled up to the orchestrator.
- `Document.Markdown` is the **only** text field on the `Document` aggregate. Consumers that need plain text strip on demand via `Dignite.Vault.Extract.Documents.MarkdownStripper.Strip(...)`; nothing is persisted in stripped form.

**Markdown-first is an engineering default, not a creed.** Out-of-band signals (coordinates, confidence, page metadata, form key-value structure, stamp positions) are **orthogonal** to Markdown. When future needs arise — citation highlighting on the source PDF, stamp localization, form key-value extraction, page-aware QA — they belong as **named, optional, strongly-typed** fields on `TextExtractionResult` (e.g. `IReadOnlyList<PageBlock>? PageBlocks`) or as a separate extractor interface orthogonal to `ITextExtractor`. **Forbidden**: stuffing such signals back into the Markdown string, or adding a `Dictionary<string, object>` extension slot. Each new out-of-band signal needs its own Issue — it's an architecture decision, not a quiet field addition.

Source contract: [`ITextExtractor`](https://github.com/dignite-projects/vault-extract/blob/main/core/src/Dignite.Vault.Extract.Abstractions/Parse/ITextExtractor.cs), [`IMarkdownTextProvider`](https://github.com/dignite-projects/vault-extract/blob/main/core/src/Dignite.Vault.Extract.Parse/IMarkdownTextProvider.cs).

## Two Extraction Paths

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
                    └─→ IOcrProvider (Vision-LLM / PaddleOCR / Azure Document Intelligence)

Both paths write the same shape: TextExtractionResult { Markdown, DetectedLanguage, NativePayload, ... }
                                  → Document.Markdown
```

The image/scan path is dispatched by file kind. The digital path is dispatched **per file by extension** across coexisting Markdown providers. Hosts wire one or more Markdown providers plus exactly one OCR provider via `[DependsOn(...)]`; switching providers is a host-level swap with no Application or Domain changes.

## Digital Extraction — Markdown Providers (Dispatched by Extension)

`IMarkdownTextProvider` implementations **coexist** and are dispatched **per file by extension** — unlike `IOcrProvider`, which is host-selected and mutually exclusive. Each provider self-declares the extensions it owns via `CanHandle(extension)` plus a `Priority`; `DefaultTextExtractor` picks the highest-priority provider that can handle the file, with ElBruno as the catch-all fallback. **Omitting a specialized provider module makes its extension fall back to ElBruno**, preserving prior behaviour.

| Provider | Owns | Notes |
|---|---|---|
| **PdfExtractor** (`Dignite.Vault.Extract.Parse.Pdf`, PdfPig) | `.pdf` | Extracts the digital text layer **and** embedded raster images. Each image is transcribed through the host-selected `IOcrProvider` and inlined into the Markdown at its reading position (#301), so embedded figures are no longer silently dropped. Vector-only graphics are an accepted blind spot (`GetImages()` does not see them). |
| **OpenXmlExtractor** (`Dignite.Vault.Extract.Parse.OpenXml`, OpenXML SDK) | `.pptx`, `.docx` | Owns the whole PowerPoint / Word pass: rebuilds structure (PPTX slide text, plus speaker notes when `IncludeSpeakerNotes` is on, #307; DOCX headings / tables / lists / inline formatting / hyperlinks / text boxes, #308) **and** transcribes embedded raster images through the host-selected `IOcrProvider`, renders `ChartPart` backing data as Markdown tables, inlining everything at reading position. EMF/WMF vector images are an accepted blind spot. |
| **ElBruno MarkItDown** (`Dignite.Vault.Extract.Parse.ElBrunoMarkItDown`) | catch-all (XLSX via its Excel plugin; HTML / TXT / CSV / TSV / RTF / EPUB; also `.docx` when the OpenXml module is absent, and `.pdf` when the Pdf module is absent) | Default fallback; enabled by the host module. XLSX conversion emits each non-empty worksheet as Markdown tables. |

**XLSX resource boundary (#471).** An XLSX is a compressed ZIP package, so the 20 MiB upload limit alone does not bound the memory or CPU required to open it. Before ClosedXML materializes the workbook, the ElBruno provider drains every archive entry through a shared expanded-byte budget and rejects packages exceeding the entry, worksheet, or cell caps. The resulting Markdown has a separate character cap. A limit violation fails the parse run explicitly; it is never persisted as a successful empty extraction.

**Embedded images in digital PDFs (#301).** PdfExtractor reuses the host-selected `IOcrProvider` for figure transcription — no separate vision client is wired at the Markdown-provider layer, and the semantics are transcription only (no chart/describe modes). Image-heavy PDFs are bounded by `PdfExtractorOptions` (`MaxImagesPerPdf`, `MinImagePixels` — tiny decorative images are skipped). When images are dropped (cap reached / undecodable codec such as JBIG2/JPX) or a figure's OCR is truncated, the result is marked incomplete via the #268 completeness signal.

**Searchable / "sandwich" PDFs (#309).** A scan-to-searchable PDF stores, per page, a full-page scan raster plus an (often invisible) OCR text layer over it. PdfExtractor detects this and **skips the full-page raster** instead of re-OCRing it, which would duplicate the already-extracted text and burn a redundant vision call per page. The skip is intentional and non-lossy — the text layer is kept and it does **not** trip the #268 completeness signal (a deliberate skip is not a dropped figure). It is high-precision and errs toward keeping: it fires only when the image's **placement bbox** covers most of the page **and** the text layer reads as a whole-page transcription (vertically spread across the image region, not a thin edge caption band); a predominantly invisible `Tr 3` layer is the canonical confirmation. When unsure it keeps and transcribes the image, so a real full-page figure (figure + caption / infographic / photo) is never dropped. Tunable via `PdfExtractorOptions` (`SkipFullPageScanBackground`, on by default; `FullPageScanCoverageThreshold`, `FullPageScanMinTextLines`, `FullPageScanMinTextVerticalCoverage`, `FullPageScanMinInvisibleTextRatio`). Tiled multi-image scan pages and heavily margined/cropped scans are out of scope for now and still double-OCR.

**Scanned / no-text-layer PDF.** If a PDF has no digital text layer, the Markdown provider returns empty Markdown — PdfExtractor does **not** OCR its images in that case — and the pipeline falls through to the whole-page `IOcrProvider` path. There is no double OCR.

**OpenXML PPTX / DOCX (#307 / #308).** OpenXmlExtractor owns the full PowerPoint / Word pass so the image↔text position linkage is preserved: it rebuilds the document structure itself and reuses the host-selected `IOcrProvider` for embedded-image transcription (no separate vision client, transcription only), renders charts and tables directly from the OpenXML (no OCR, no vector blind spot), and uses native alt-text (`docPr/@descr`) as the figure caption. A figure (image or chart) inside a table cell is extracted as its own block after the table, since a Markdown cell cannot host a transcription. List nesting is indented three spaces per level so a child under an ordered (`1.`) item still nests in CommonMark. Block-level content controls (`w:sdt`) and custom-XML wrappers are recursed into (in body paragraphs and table cells alike), and legacy VML raster images (`w:pict`/`v:imagedata`) are transcribed alongside DrawingML ones, so neither is silently dropped; an unhandled block — or content-control nesting beyond a safety depth cap — that still carries text trips the completeness signal. Known limitation: VML images are not decorative-size-filtered (a VML shape's size lives in its style attribute, not `wp:extent`), so a tiny VML icon may be transcribed where its DrawingML equivalent would be skipped. DOCX collapses markup-compatibility (`mc:AlternateContent`) on open so a modern text box / picture is not read twice, and applies the accepted view of tracked changes (insertions kept, deletions dropped). Image caps live in `OpenXmlExtractorOptions`; dropped / undecodable images, truncated figure OCR, image-cap hits, unrenderable charts, and per-block parse failures trip the #268 completeness signal. Unlike PDF there is no whole-page OCR fallback, so once the module owns an extension an unopenable file reports empty + incomplete. **Required for `.pptx`** (ElBruno has no PresentationML converter); `.docx` degrades gracefully to ElBruno when the module is absent.

## OCR — Choosing a Provider

Dignite Vault Extract ships three OCR providers. Enable **one** in the host module's `[DependsOn]` (in this repository, `host/src/VaultExtractHostModule.cs`, together with the matching `<ProjectReference>`) based on the deployment scenario — `IOcrProvider` has a single registration, so the providers are mutually exclusive. The shipped host enables **Vision-LLM**.

| | [Vision-LLM](ocr-vision-llm.md) (shipped default) | [PaddleOCR](ocr-paddleocr.md) | [Azure Document Intelligence](ocr-azure-document-intelligence.md) |
|---|---|---|---|
| Where data goes | Your LLM provider (stays local if the model does) | Local sidecar — never leaves the network | Cloud (Azure region) |
| Setup | Host LLM endpoint + a vision model id | `docker compose up paddleocr` | Azure subscription + AI resource |
| Markdown output | Native (the LLM transcribes to Markdown) | Native (PP-StructureV3 / VL); flat (PP-OCRv4) | Native |
| Cold start | Instant | ~30–60 s first run (model download ~600 MB) | Instant |
| Per-page cost | Paid per token (image + output) | Free | F0 free tier / S0 ~$1.50 / 1000 pages |
| Best at | **Phone photos / thermal receipts** where layout OCR fails (#259); image-only PDFs | Structured scans, CJK documents | Structured scans, forms |

> Cloud LLM OCR (Gemini / Mistral) and Google Document AI were evaluated and rejected — see issue #79 for the rationale (Japanese-language quality, region access, dependency footprint, free-tier shape). The [vision-LLM provider](ocr-vision-llm.md) (#259) is a later, vendor-agnostic `IChatClient`-based path for the photo/receipt scenario specifically.

Each provider's setup lives on its own page: **[Vision-LLM](ocr-vision-llm.md)** · **[PaddleOCR](ocr-paddleocr.md)** · **[Azure Document Intelligence](ocr-azure-document-intelligence.md)**.

### Provider-Agnostic OCR Pipeline Behavior

These apply regardless of which provider is wired:

- Dignite Vault Extract does not auto-switch OCR profiles per document. The OCR provider runs once with the host-configured model; there is no second OCR pass with a guessed specialized mode, and OCR average confidence is no longer a quality gate (#196).
- OCR completion always advances the document to classification. OCR average confidence was removed (#196): it did not reliably predict real quality (skewed pages, blurry scans and layout issues are not reflected in an average score). Quality problems surface later as review reasons (an unresolved classification, a suspected duplicate, missing or invalid field values), which the operator resolves, or fixes by re-parsing the document. See [Documents and Review](../documents/documents.md).
- **Extraction completeness (#268).** A provider may report that it captured only part of a document (e.g. a vision-LLM output truncated at the token cap, or a dropped PDF page). This is carried on `TextExtractionResult.IsComplete` / `IncompleteReason`, archived into `Document.ExtractionMetadata`, and surfaced to downstream consumers on the REST `DocumentDto` as `ExtractionIsComplete` + `ExtractionIncompleteReason`. It is a **quality signal** (distinct from internal extraction provenance, which is not exposed); the channel does not gate Ready on it — consumers decide whether to accept, downgrade, or route to review. Providers that don't report it default to complete.

## Extractor Options

The digital-document providers bound their work with options bound from configuration.

**`PdfExtractor`** (`PdfExtractorOptions`, package `Dignite.Vault.Extract.Parse.Pdf`):

| Key | Default | Description |
| --- | --- | --- |
| `MaxImagesPerPdf` | `50` | Most embedded images transcribed per PDF; beyond it the result is marked incomplete |
| `MinImagePixels` | `4096` (64 × 64) | Smaller images are treated as decorative and skipped |
| `SkipFullPageScanBackground` | `true` | Skip the full-page scan raster of a searchable ("sandwich") PDF instead of OCR-ing it again |
| `FullPageScanCoverageThreshold` | `0.9` | Share of the page an image must cover to count as a full-page scan |
| `FullPageScanMinTextLines` | `15` | Text lines the page must carry to count as a whole-page transcription |
| `FullPageScanMinTextVerticalCoverage` | `0.5` | Share of the image's height the text must span |
| `FullPageScanMinInvisibleTextRatio` | `0.6` | Share of invisible (`Tr 3`) text that confirms a scan layer on its own |
| `ReconstructTables` | `true` | Rebuild detected grids as Markdown tables |

**`OpenXmlExtractor`** (`OpenXmlExtractorOptions`, package `Dignite.Vault.Extract.Parse.OpenXml`):

| Key | Default | Description |
| --- | --- | --- |
| `MaxImagesPerFile` | `50` | Most embedded images transcribed per file; beyond it the result is marked incomplete |
| `MinImagePixels` | `4096` (64 × 64) | Images displayed smaller than this are skipped as decorative |
| `MaxImageBytesPerImage` | `16777216` (16 MiB) | Larger images are not decoded (decompression-bomb guard) |
| `IncludeSpeakerNotes` | `false` | Include PowerPoint speaker notes in the Markdown |

## Adding a Custom OCR / Digital Provider

Implement `IOcrProvider` (for image/scan input) or `IMarkdownTextProvider` (for files with a digital text layer). Both contracts are documented in their source files; both demand Markdown output. A Markdown provider also declares which extensions it owns via `CanHandle(extension)` + `Priority` (use a non-negative priority for a specialized provider; the catch-all fallback sits at `MarkdownProviderPriorities.Fallback`).

The provider lives in its own module project (`Dignite.Vault.Extract.Ocr.<Vendor>` or `Dignite.Vault.Extract.Parse.<Vendor>`) and is enabled by the host through `[DependsOn(...)]`. Markdown providers coexist (dispatched per file by extension), so adding one does not displace the others.

**Markdown-first responsibility is on the provider, not the orchestrator.** The `OcrResult` and `TextExtractionResult` types expose only a `Markdown` field — there is no parallel `RawText` channel. If the underlying OCR engine returns plain text only (e.g. PaddleOCR PP-OCRv4), the provider itself must wrap paragraphs into flat Markdown (typically `string.Join("\n\n", paragraphs)`). Returning empty Markdown when the engine produced text is a contract violation. Custom OCR providers should expose their model choice through provider/host configuration, not through Dignite Vault Extract core profile codes.

Custom OCR provider projects only need to reference `Dignite.Vault.Extract.Ocr` — they do not need (and should not pull in) `Dignite.Vault.Extract.Parse` or `Dignite.Vault.Extract.Abstractions`.

## See Also

- OCR providers: [Vision-LLM](ocr-vision-llm.md) · [PaddleOCR](ocr-paddleocr.md) · [Azure Document Intelligence](ocr-azure-document-intelligence.md)
- [Classification pipeline](../pipeline/classification.md) — how the LLM consumes the Markdown
- [AI provider](../configuration/ai-provider.md) — provider wiring for the keyed chat clients used by classification / field extraction / title generation
- [Deployment checklist](../deployment/deployment-checklist.md) — verifying OCR after a sidecar upgrade

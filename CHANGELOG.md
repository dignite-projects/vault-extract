# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-06-20

### Added

- **Container document concept + born-digital segmentation** (#346) — the pipeline now detects "container" documents (bundles that wrap several independent documents) at classification and handles them as a first-class case. A detected container suppresses type-bound field extraction (a bundle has no single type) and does not enter the operator review queue. Born-digital containers are automatically segmented: one LLM pass identifies verbatim start markers, `MarkdownSlicer` cuts the original Markdown deterministically (the LLM never regenerates content), and each constituent slice is spawned as a derived `Document` via the same `DerivedDocumentSpawner` sink used by figure routing. Sub-documents carry an `OriginDocumentId` / `OriginConstituentKey` back-reference and can be filtered by `OriginDocumentId` through REST and Angular. Sub-documents are retracted when their container is reclassified to a concrete type, and the reverse (`DocumentReclassifiedToContainerEto`, #355) fires when a previously concrete-typed document is re-recognized as a container, giving downstream consumers a symmetric signal to retract their records. Provenance (`IsContainer`, `OriginDocumentId`) is surfaced on `DocumentDto`, `DocumentReadyEto`, MCP resources, and the Angular document list.

- **Figure sub-document routing** (#306) — embedded raster figures extracted from digital PDFs (#301, see below) that are themselves documents are now routed as derived `Document` instances rather than staying only as inline transcription. A `DocumentFigure` ledger persists each figure as a Pending or Spawned candidate (idempotent, unique `(SourceDocumentId, FigureKey)`), and `DocumentFigureRoutingJob` classifies each candidate against the source document's tenant type layer; figures that clear the matched type's `ConfidenceThreshold` are spawned as derived Documents with the figure crop copied to an independent blob. Figures that are not documents stay inline in the source Markdown unchanged. `OCRCompletedEto` now carries `FigureOcrCount` (the number of dispatched embedded-image OCR calls, including ones that threw) so downstream consumers can track extraction completeness at the event level.

- **`Dignite.Vault.Extract.Parse.Pdf` module** (#301) — digital PDFs (text layer + embedded bitmaps) no longer silently drop their images. The new `PdfExtractor` (PdfPig) extracts the digital text layer and transcribes each embedded raster image via the host-selected `IOcrProvider`, inlining the transcription into the Markdown at its reading position. `IMarkdownTextProvider` gains `CanHandle(ext)` + `Priority` so multiple providers for the same extension can coexist without ambiguity.

- **`Dignite.Vault.Extract.Parse.OpenXml` module** (#307, #308) — stops silently dropping rich content from Office files:
  - **PPTX**: `PptxExtractor` produces slide text (titles as headings), embedded raster images (transcribed via `IOcrProvider`), chart backing data reconstructed as Markdown tables, native `<a:tbl>` tables, and optionally speaker notes — all inlined in slide/shape reading order. Charts with a divergent category axis are dropped and trip the `#268` completeness signal rather than rendering misaligned data.
  - **DOCX**: `DocxExtractor` replaces the MarkItDown path for `.docx` and adds full OpenXML structural rebuild: headings, lists, tables, and embedded raster images (transcribed via `IOcrProvider`) are all faithfully represented in the Markdown output.

- **Column-aware PDF reading order** (#310) — `PdfExtractor` now segments each page into layout blocks via PdfPig's `RecursiveXYCut` + `UnsupervisedReadingOrderDetector` before rendering, eliminating the spliced-sentence artifact produced by multi-column pages under the previous flat top→bottom / left→right sort. Figures are merged into the block order by nearest-block centroid so figure inlining and caption association continue to work. A single-region fallback engages on any segmenter fault so no content is ever lost.

- **PDF digital-layer table reconstruction** (#329) — `PdfExtractor` reconstructs tables found in a PDF's digital text layer into Markdown table syntax, so downstream consumers receive properly delimited rows and columns rather than a flat character stream.

- **Document overview statistics** (#333) — `IDocumentStatisticsAppService.GetAsync` returns per-lifecycle counts (uploaded / processing / ready / failed), needs-review count, and total original upload size for the current ambient layer. Gated by `Documents.Default`. Surfaced on the new Angular overview page.

- **Angular UI updates** — the Angular operator SPA gains: an upload-first home page that replaces the generic ABP landing screen (#332); cabinet and document type overview cards on the home screen (#335); a `/documents/overview` route with the statistics dashboard and sidebar entry; and a "view sub-documents" filter on the document list for container documents (#354).

- **Test projects** (#302) — four new test projects close the v0.1.0 known-limitation gap: `TextExtraction.Tests` (unit tests for `DefaultTextExtractor` orchestrator dispatch, `CanHandle`+`Priority` selection, scanned-PDF OCR fallback, and native-payload mapping), `Ocr.PaddleOcr.Tests`, `Ocr.AzureDocumentIntelligence.Tests`, and `HttpApi.Tests`.

### Changed

- **Package rename: `Dignite.DocumentAI` → `Dignite.Vault.Extract`** (#370, **breaking**) — all NuGet package IDs, namespaces, `[RemoteServiceName]`, `ConnectionStringName`, Angular package names (`@dignite/document-ai` → `@dignite/vault-extract`), permission group constants, OpenIddict audience/scope, and DB table prefix (`DocAI` → `Extract`) have been renamed in a clean cutover with no compatibility shims. Consumers must update their `PackageReference` entries, `using` directives, Angular imports, and any hardcoded table or permission string references. The `TextExtraction` sub-namespace in the parse-stack projects was simultaneously renamed to `Parse` (`Dignite.Vault.Extract.Parse.*`).

- **Cross-database portability for layer-scoped uniqueness** (#304) — replaced the four soft-delete-filtered unique indexes on `DocumentTypes` (`TenantId, TypeCode`), `FieldDefinitions` (`TenantId, DocumentTypeId, Name`), `ExportTemplates` (`TenantId, Name`), and `Cabinets` (`TenantId, Name`) with application-layer uniqueness enforcement in dedicated domain services (`DocumentTypeManager` / `FieldDefinitionManager` / `ExportTemplateManager` / `CabinetManager`). The schema now emits no provider-specific index DDL (no `HasFilter("IsDeleted = 0")` literal, no reliance on SQL Server's "unique index treats NULLs as equal" semantics) and applies cleanly on SQL Server and PostgreSQL — removing the v0.1.0 SQL-Server-only baseline caveat. Layer-scoped uniqueness (same key allowed across the Host / tenant layers, rejected within a layer), the soft-delete-aware `delete → recreate → restore` semantics, and the egress `(TenantId, DocumentTypeCode)` contract are all unchanged; the only behavioral change is an accepted TOCTOU race window on these low-frequency, admin-managed configuration entities.

- **Skip full-page scan background of searchable/sandwich PDFs** (#309) — `PdfExtractor` detects and skips re-OCR'ing the full-page raster in a searchable (sandwich) PDF, eliminating the redundant vision call per page and the duplicated text that resulted. The guard errs toward keeping: both the placement bbox must cover ~the whole page AND the text layer must read as a whole-page transcription (many lines or a predominantly invisible Tr 3 layer) before the background is skipped. The text layer is always kept and no completeness signal is tripped on a skip.

### Fixed

- Markdown metacharacters in extracted source text (backticks, asterisks, underscores, etc.) are now escaped before being embedded in generated Markdown output, preventing formatting corruption in the downstream Markdown (#320).
- Inlined figure transcription slices from the source document are suppressed during born-digital container segmentation so they do not appear as phantom constituent documents (#359).
- On container reclassification to a concrete type, only the segmentation-spawned sub-documents are retracted; figure-routed sub-documents (which belong to the document's own content, not to the segmentation) are correctly left in place (#364).
- `IsSegmented` is cleared on the container when an automatic container→concrete-type reclassification occurs, so a subsequent re-classification can re-trigger segmentation if the document is later reclassified back to a container (#371/#377).
- Closed a recall gap where embedded figures near the edge of the extraction window were silently dropped rather than being persisted as routing candidates (#379).

### Resolved known limitations

- Test coverage gap (#302) — test projects for `TextExtraction`, `Ocr.PaddleOcr`, `Ocr.AzureDocumentIntelligence`, and `HttpApi` are now included (v0.1.0 listed this as a known limitation).

## [0.1.0] - 2026-06-13

First public release of Dignite Vault Extract — a **channel layer** that turns physical paper / scans / photos / PDF images / Office files into trustworthy digitized data (Markdown + structured metadata) for downstream RAG platforms, business systems, and AI clients. See [CLAUDE.md](./CLAUDE.md) for the full positioning and architecture contract.

### Added

- **Document ingestion** — upload API with BlobStore-backed original-file storage (`FileOrigin`: original file name, size, content type, SHA-256 content hash) and a recycle bin (soft delete / restore / permanent delete; permanent delete also removes the original file and archive blobs).
- **Markdown-first text extraction** — the `DefaultTextExtractor` orchestrator dispatches by file extension: digital documents go through the ElBruno MarkItDown Markdown provider (PDF / Word / HTML / plain text / CSV / RTF / EPUB, …); images and PDFs without a text layer go through OCR. Three interchangeable OCR providers, exactly one enabled in the host:
  - **Vision LLM** (current host default, #259) — multimodal-`IChatClient` OCR for phone photos, thermal receipts, and image-only PDFs;
  - **PaddleOCR** — local Docker sidecar (PP-StructureV3, CPU; data never leaves the network);
  - **Azure Document Intelligence** — cloud (`prebuilt-layout`).
  Provider-native payloads (bbox / table cells / confidence) are archived to blob storage with a minimal provenance manifest (#210), and extraction-completeness quality signals (`ExtractionIsComplete` / `ExtractionIncompleteReason`) are exposed to downstream consumers (#268).
- **Automatic title generation and language detection** as part of the text-extraction pipeline.
- **LLM document classification** — documents are classified against self-managed document types in a strict two-layer model (Host layer / per-tenant layer; no cross-layer union, no built-in types), with per-type confidence thresholds, a manual review queue (reclassify / reject), and a **Ready gate**: `DocumentReadyEto` is published only once the document's type is confirmed.
- **Type-bound field extraction (field architecture v2)** — per-type field definitions on either layer, one LLM extraction pass per document, values persisted as typed `DocumentExtractedField` rows and exposed as `ExtractedFields` through REST and MCP.
- **Cabinets** — a manual grouping dimension orthogonal to document types ("which batch / group" vs. "what is this"), with optional AI-assisted cabinet suggestion at upload (#265).
- **Multi-stage integration events** — `DocumentUploadedEto` / `OCRCompletedEto` / `DocumentClassifiedEto` / `FieldsExtractedEto` / `DocumentReadyEto`, plus lifecycle events `DocumentDeletedEto` / `DocumentRestoredEto` / `DocumentPermanentlyDeletedEto`. Published through ABP's transactional outbox: at-least-once delivery, thin payloads, `EventTime`-based consumer-side idempotency.
- **REST API exit** — HTTP API with Swagger UI, secured by OpenIddict Bearer authentication.
- **MCP server exit** — Streamable HTTP endpoint at `/mcp` reusing the host's OpenIddict Bearer auth (including RFC 9728 protected-resource-metadata discovery); document and document-type resources (`extract://documents/{id}`) plus read-only tools `search_extract_documents`, `get_document`, and `list_document_types`, each with fail-closed permission assertions.
- **Export templates** — per-tenant CSV / XLSX file egress for systems that can only ingest files: field projection, rename, and ordering only — zero business transformation.
- **Bulk reprocessing** — operator-triggered batch reclassification and batch field re-extraction after configuration changes, plus per-document field re-extraction.
- **Pipeline run history** — `DocumentPipelineRun` records per-stage execution status, attempts, and diagnostics payloads.
- **Operator Angular UI** — Angular 21 / Nx workspace SPA built on ABP Angular modules: document management and review, document types, field definitions, export templates, cabinets, and reprocessing.
- **Observability** — OpenTelemetry traces and metrics from the `Microsoft.Extensions.AI` / agent stack through a host-configured OTLP export pipeline (aspire-dashboard for local development).

### Known limitations

- **Test coverage gaps** — there are no test projects yet for the Parse orchestrator, `Ocr.PaddleOcr`, `Ocr.AzureDocumentIntelligence`, or `HttpApi`.
- **Webhook exit is not yet implemented** — the exit contract names four exits (REST / MCP server / EventBus / Webhook); this release ships the first three.
- **MCP server is pull-only** — no resource subscriptions or lifecycle notifications yet (follow-up increment, #197).

[Unreleased]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dignite-projects/vault-extract/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/dignite-projects/vault-extract/releases/tag/v0.1.0

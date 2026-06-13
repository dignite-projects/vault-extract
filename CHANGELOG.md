# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-06-13

First public release of Dignite Document AI — a **channel layer** that turns physical paper / scans / photos / PDF images / Office files into trustworthy digitized data (Markdown + structured metadata) for downstream RAG platforms, business systems, and AI clients. See [CLAUDE.md](./CLAUDE.md) for the full positioning and architecture contract.

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
- **MCP server exit** — Streamable HTTP endpoint at `/mcp` reusing the host's OpenIddict Bearer auth (including RFC 9728 protected-resource-metadata discovery); document and document-type resources (`docai://documents/{id}`) plus read-only tools `search_docai_documents`, `get_document`, and `list_document_types`, each with fail-closed permission assertions.
- **Export templates** — per-tenant CSV / XLSX file egress for systems that can only ingest files: field projection, rename, and ordering only — zero business transformation.
- **Bulk reprocessing** — operator-triggered batch reclassification and batch field re-extraction after configuration changes, plus per-document field re-extraction.
- **Pipeline run history** — `DocumentPipelineRun` records per-stage execution status, attempts, and diagnostics payloads.
- **Operator Angular UI** — Angular 21 / Nx workspace SPA built on ABP Angular modules: document management and review, document types, field definitions, export templates, cabinets, and reprocessing.
- **Observability** — OpenTelemetry traces and metrics from the `Microsoft.Extensions.AI` / agent stack through a host-configured OTLP export pipeline (aspire-dashboard for local development).

### Known limitations

- **Test coverage gaps** — there are no test projects yet for the TextExtraction orchestrator, `Ocr.PaddleOcr`, `Ocr.AzureDocumentIntelligence`, or `HttpApi`.
- **SQL Server is the host baseline** — the filtered unique indexes (`IsDeleted = 0`) on `DocumentTypes` / `FieldDefinitions` / `ExportTemplates` / `Cabinets` rely on SQL Server's "unique indexes treat NULLs as equal" semantics to enforce Host-layer (`TenantId IS NULL`) uniqueness. PostgreSQL defaults to `NULLS DISTINCT`, and the `HasFilter` literal is not portable — moving to another database requires re-evaluating these indexes. See [docs/deployment.md](./docs/deployment.md).
- **Webhook exit is not yet implemented** — the exit contract names four exits (REST / MCP server / EventBus / Webhook); this release ships the first three.
- **MCP server is pull-only** — no resource subscriptions or lifecycle notifications yet (follow-up increment, #197).

[Unreleased]: https://github.com/dignite-projects/document-ai/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/dignite-projects/document-ai/releases/tag/v0.1.0

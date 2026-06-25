# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0-preview.2] - 2026-06-25

Preview of the 0.2.0 line. This release rebrands the project to **Dignite Vault Extract** and is dominated by the container / sub-document model and a major expansion of structure-aware text extraction (PDF / DOCX / PPTX). As a `0.y.z` pre-release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Changed

- **BREAKING — package identifiers renamed to the `Dignite.Vault.Extract.*` prefix** (consolidating the earlier `Dignite.DocumentAI` → `Dignite.Extract` cutover, #370 / #382). NuGet package IDs and namespaces moved, and the Angular library is now published as `@dignite/vault-extract`. Downstream consumers must update package references, `using` directives, and npm dependencies.
- Rebranded the UI to the DIGNITE badge — new logos and favicons, restored default ABP typography (removed the ProstoOne font).
- Collapsed the review queue into a list filter + detail remediation flow (#395).
- Enforce document-type layer-scoped uniqueness in the application layer (#304).

### Added

- **Container & sub-document model** — a document can now be recognised as a *container* and segmented into derived sub-documents, with full provenance on the MCP and Angular egress:
  - Container document concept and born-digital container segmentation (#346, #347, #351).
  - Route embedded figures to derived sub-documents and persist them as Scenario B candidates (#306, #344, #345); parent-aware figure gate (#365, #369); unified figure routing + born-digital segmentation (#371, #375).
  - Sub-document discovery by `OriginDocumentId` and a "view sub-documents" container filter (#354, #360, #363).
  - Retract sub-documents when a container is reclassified, and emit `DocumentReclassifiedToContainerEto` on type→container re-recognition (#349, #352, #355, #362).
- **Structure-aware text extraction**:
  - Extract embedded raster images from digital PDFs via `IOcrProvider` (#301, #311).
  - DOCX embedded-image extraction + OpenXML-to-Markdown structural rebuild (#308, #323).
  - PPTX embedded images, charts, tables & speaker-notes extraction via OpenXML (#307, #314).
  - Column-aware PDF reading order via PdfPig document-layout analysis (#310, #326); reconstruct digital-layer tables into Markdown tables (#329, #340).
  - Map PDF font size/weight to Markdown headings and carry bold/italic runs into emphasis (#403); strip running headers/footers and page numbers (#383); keep figure OCR markers as egress provenance annotations (#381); skip the full-page scan background of searchable / sandwich PDFs (#309, #324).
- Duplicate re-upload detection via field fingerprint, gating the `DocumentReadyEto` event (#411).
- Document AI overview statistics (#333, #341) with cabinet and document-type overview cards (#335, #342) and an upload-first home page (#332, #339).
- Initial EF Core database migration for the host.

### Fixed

- PDF reading-order and table reconstruction: band-aware ordering so prose around tables isn't scrambled, key-value tables under titles / stamps, paragraph reconstruction in loosely-leaded PDFs, and stop digital-PDF tables linearizing when wrapped by body text (#407 and related).
- Escape source-text Markdown metacharacters in generated output (#320, #337).
- Keep document titles / headings out of the OCR running-header exclusion (#409).
- Block deleting a source that still has live sub-documents and harden the orphan read path; exclude soft-deleted rows from the sub-document unique index (#391).
- i18n: add the field-extraction pipeline label and complete the zh-Hant / ja pipeline keys.
- Angular: persist list filters / paging in the URL, restore return-navigation from document detail, and update footer branding.

### Removed

- Legacy Angular document-upload route.
- Dead fields from the segmentation subsystem (#390).

[Unreleased]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0-preview.2...HEAD
[0.2.0-preview.2]: https://github.com/dignite-projects/vault-extract/compare/v0.1.0...v0.2.0-preview.2

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- **MCP API-key channel hardening** (follow-ups to #428 / #430): the `/mcp` endpoint is now **rate-limited** per client IP — covering both the API-key channel and the OAuth discovery `401` path — and a present-but-invalid key raises a rate-limited security `Warning` (source IP + header name, never the value) (#433). API keys can be configured as a **SHA-256 `KeyHash`** (hash-at-rest) instead of plaintext, so a config/secret-store leak no longer exposes usable keys (#435). An opt-in host seed (`Mcp:ApiKey:SeedServiceAccounts`) **enforces least privilege** on each configured service account — applying exactly the `VaultExtract.Documents` grant and failing startup if the account is missing, over-privileged, or holds any role (#434).

### Added

- A full ABP integration test for the MCP API-key channel: a key-authenticated service-account principal resolves permissions through the real ABP permission checker (granted → search returns rows; ungranted → fail-closed `AbpAuthorizationException`) (#432).

## [0.2.0] - 2026-07-01

First stable release of the 0.2.0 line. Headlined by the rebrand to **Dignite Vault Extract**, the container / sub-document model, and a major expansion of structure-aware text extraction (PDF / DOCX / PPTX). The granular per-preview history is retained in the `0.2.0-preview.*` sections below.

> **Upgrading from 0.1.0 is breaking**: NuGet package IDs and namespaces moved to the `Dignite.Vault.Extract.*` prefix, the Angular library is now `@dignite/vault-extract`, and the C# module / type prefix is `VaultExtract`. See the Changed entries below. As a `0.y.z` release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Added

- **Container & sub-document model** — a document can be recognised as a *container* and segmented into derived sub-documents, with full provenance across the MCP and Angular egress and `OriginDocumentId` on the events (#346, #347, #351, #354, #360, #363, #371, #375).
- **Structure-aware text extraction** — embedded raster/image extraction from digital PDF, DOCX, and PPTX via `IOcrProvider` / OpenXML; column-aware PDF reading order; digital-layer and lattice (ruled) table reconstruction into Markdown tables; PDF font size/weight → Markdown headings; running header/footer stripping; skip the full-page scan background of searchable / sandwich PDFs; and figure OCR markers kept as egress provenance annotations (#301, #311, #308, #323, #307, #314, #309, #324, #310, #326, #329, #340, #403, #383, #381, #450).
- **Static API-key fallback authentication for the `/mcp` egress**, alongside OpenIddict Bearer and the OAuth discovery flow, for clients that cannot run the dynamic OAuth flow (#430, closes #428). MCP discovery is now a one-call `AddExtractMcpDiscovery(...)` extension (#422).
- **Duplicate re-upload detection** via field fingerprint, gating `DocumentReadyEto` (#411).
- Angular: live pipeline status on document detail via interim polling (#442); render LongText extracted-field values as Markdown (#418); document AI overview statistics with cabinet / document-type overview cards and an upload-first home page (#333, #341, #335, #342, #332, #339).

### Changed

- **BREAKING — package identifiers renamed to the `Dignite.Vault.Extract.*` prefix**, and the Angular library is published as `@dignite/vault-extract` (#370, #382).
- **BREAKING — C# type and module prefix unified from `Extract` to `VaultExtract`** (`VaultExtractDomainModule`, `VaultExtractDbContext`, `VaultExtractErrorCodes`, …), matching the `Dignite.Vault.Extract` namespace and ABP convention. Namespaces, the `Extract` extraction *verb*, and every serialized contract (error codes `Extract:*`, DB table prefix, config sections, blob container, localization resources) are unchanged (#438).
- Rebranded the UI to the DIGNITE badge; reduced to four supported languages and aligned the localization files to a common layout.
- OCR recognition language is now provider-specific; removed the dead central `VaultExtractOcrOptions` layer (#441).
- Enforce document-type layer-scoped uniqueness in the application layer (#304).

### Fixed

- PDF reading-order and table reconstruction hardening: band-aware ordering, robustness to narrow gutters / sparse / empty columns, and key-value tables under titles / stamps (#407, #446 and related).
- Unwrap stray Markdown code fences from VisionLlm OCR so tables render (#448).
- Block deleting a source that still has live sub-documents, and harden the orphan read path (#391).
- Keep document titles / headings out of the OCR running-header exclusion (#409).
- Escape source-text Markdown metacharacters in generated output (#320, #337).
- Angular: dark-theme-aware document detail, home context panel, and upload drop-zone; reason-aware review banner with a complete-fields action; localize `ExportFormat` / `FieldDataType` list labels; persist list filters / paging in the URL.

### Removed

- Legacy Angular document-upload route and dead segmentation fields (#390).
- Dead central OCR options layer (#441) and the `pack-all.ps1` packaging script.

### Security

- Bumped Angular to 21.2 and patched dev-dependency CVEs (#425).

## [0.2.0-preview.4] - 2026-06-26

### Changed

- **Unified the C# type and module name prefix from `Extract` to `VaultExtract`**, matching the `Dignite.Vault.Extract` namespace and ABP's own convention (`Volo.Abp.Identity` → `AbpIdentity*Module`) — `VaultExtractDomainModule`, `VaultExtractApplicationModule`, `VaultExtractDbContext` / `IVaultExtractDbContext`, `VaultExtractErrorCodes`, `VaultExtractPermissions`, and the `ConfigureVaultExtract` / `AddVaultExtractMcpDiscovery` / `UseVaultExtractMcpApiKey` extension methods, among others. **Breaking for consumers that reference the module or type names directly** — update `[DependsOn(typeof(ExtractXxxModule))]`, `IExtractDbContext`, and any base-class references to the `VaultExtract*` names. Namespaces, the `Extract` extraction *verb* (`ExtractedField` / `ITextExtractor` / `FieldExtraction*`), and every serialized contract (error codes `Extract:*`, DB table prefix, config sections, blob container, localization resources) are unchanged (#438).
- The release workflow now emits the npm UI tarball alongside the NuGet packages in a single run, so each release produces both backend and frontend artifacts.

## [0.2.0-preview.3] - 2026-06-26

### Added

- **Static API-key fallback authentication for the `/mcp` egress** — a path-scoped fallback that runs alongside OpenIddict Bearer and the #278 OAuth discovery flow, for clients that cannot run the dynamic OAuth flow but can send a static header (OpenAI Codex, ABP AI Management). Constant-time key match maps to a least-privilege service-account principal, `RequireHttps` is enforced, and it fails open on a miss so Bearer + discovery are untouched. Disabled by default (empty `Mcp:ApiKey:Keys`) (#430, closes #428).
- **MCP discovery wiring is now a reusable one-call extension** — the #278 OAuth Protected Resource Metadata discovery flow (RFC 9728) moved from the host into the `Dignite.Vault.Extract.Mcp` egress module and is exported as `IServiceCollection.AddExtractMcpDiscovery(...)`, so any host deploying the MCP egress enables discovery with a single call instead of re-authoring the authorization result handler (#422).

### Changed

- Bumped Angular to 21.2 and patched dev-dependency CVEs (#425).

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

[Unreleased]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dignite-projects/vault-extract/compare/v0.1.0...v0.2.0

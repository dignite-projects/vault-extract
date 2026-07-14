# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-07-14

Second stable release of the channel. The 0.3.0 line adds **field validation warnings** as a first-class extraction output, brings **multi-tenancy** to the host (tenant administration, tenant-correct background jobs, and a tenant-scoped MCP surface), broadens **ingestion** to born-digital formats, deepens **OpenXML / PDF** structure extraction, repositions the **export** flow onto the document list, and puts a **size ceiling on every text that crosses an LLM boundary**. The MCP egress returns to **OAuth-only** and becomes additively extensible by downstream modules. The granular per-preview history is retained in the `0.3.0-preview.*` sections below.

> As a `0.y.z` release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases). Upgrading from 0.2.0 is backward-compatible at the package level, but deployments must apply the new EF Core migrations (tenant management, `PendingReview`, and the field-validation-warning table) before running the new binaries.

### Added

- **Field validation warnings** — field extraction now returns, in one structured LLM response, the extracted value **and** strongly-typed validation warnings. Warnings persist as a `Document` child collection, raise a new blocking review reason (`FieldValidationWarning`) that withholds `DocumentReadyEto`, surface on the REST detail and in the operator UI, and are cleared by a clean re-extraction or an explicit operator "mark resolved" action. Generic and business-type-independent — the channel presets no domain rules; architecturally the fourth instance of the `DuplicateSuspected` blocking-review pattern (#527).
- **Multi-tenancy in the host** — the host application now includes ABP Tenant Management (EF mapping, migration, and Angular routes) so host operators can administer tenants from the deployed UI (#522). The MCP egress accepts an optional `tenantId` on `search_documents` / `get_document` / `list_document_types` / `list_cabinets` and returns tenant-scoped resource URIs, preserving the selected layer as clients follow MCP links (#519 and follow-ups).
- **Digital upload formats** — accept CSV / TSV, DOCX, plain-text, and XLSX uploads through the same pipeline as scans and PDFs (#471).
- **The MCP surface is additively extensible by downstream modules** — a downstream ABP module appends its own tools via `AddMcpServer().WithTools<T>()` and its own `resources/list` categories via `VaultExtractMcpOptions.ResourceListContributors`, without forking; the open-source surface stays strictly single-tenant (#475, #476). Cabinets are exposed as a discovery resource and `search_documents` can scope to one (#473); `search_documents` results carry explicit `totalCount` / `truncated` (#445).
- **Document-type configuration packs** — export a document type with its field definitions as a portable pack and import it into another layer or deployment, driven from the operator UI with local shape validation, a preview, and create-and-update / create-only reconciliation (#444, #513).
- **"Data Download" export** — the export flow is repositioned onto the document list, defaults to all of the type's fields, filters by extracted field values, and adds an "export current view" toolbar action (#414, #496); an extracted-field-value filter is available on the operator document list (#415).
- **Field-definition prompts accept Markdown with an AI-polish action** — a Markdown prompt editor and an AI "polish" endpoint; the former prompt-length cap is dropped (#447).
- **`DocumentLifecycleStatus.PendingReview`** — a document whose pipelines have run as far as they can but that still carries a blocking review reason derives to `PendingReview` instead of `Processing`, giving the operator UI an honest, non-spinner status without changing the egress gate (#510).

### Changed

- **A size ceiling now bounds every text crossing an LLM boundary** (#491). Field extraction and segmentation **gate** above the ceiling — reaching a terminal review state rather than extracting from a truncated prefix, and never rethrowing an oversized body into the job-retry loop; classification / title / cabinet suggestion and the MCP document body **truncate** surrogate-safely and announce the cut. Prompt ceilings are host configuration (`VaultExtractBehaviorOptions`); the MCP egress ceiling is a compile-time `const`.
- **Document pipeline background jobs run in the document's tenant context** — parse, classification, field extraction, segmentation, and cabinet-suggestion jobs no longer leak through ambient or missing tenant state, with regression coverage across the reprocessing dispatch paths (#521).
- **One figure format across PDF and OpenXML** — OpenXML figure transcriptions are wrapped in the same `ImageOcrMarkup` markers the PDF path emits, so downstream sees a single figure representation (#480).
- **Deeper OpenXML structure extraction** — DOCX custom-style heading levels (#316), footnotes / endnotes (#315), and per-instance figure walking (#322); PPTX group-transform / layout-inherited reading order and group scale composition (#313, #456); `mc:AlternateContent` shapes no longer silently skipped (#319); heading resolution and figure traversal memoized per document (#458, #318).
- **Build / packaging** — SourceLink, deterministic CI builds, and NuGet symbol packages; NuGet and npm license metadata aligned with the LGPL-3.0-only LICENSE.

### Fixed

- **Concurrent-upload deadlock** — documents uploaded concurrently no longer deadlock in the pipeline (#533).
- Non-BOM legacy-encoded `.csv` / `.tsv` / `.txt` no longer decode as UTF-8 and land in `Document.Markdown` as U+FFFD garbage (#493).
- Restore the embedded-document route that the segmentation rework left detected-but-unroutable, as a Markdown slice (#494); restore the sub-document delete guard on both delete paths (#508).
- Field extraction is scheduled transactionally at classification completion, closing a premature-`Ready` race, and a reused pipeline run clears its stale status message on retry (#527).
- DOCX note-body hyperlinks resolved against the owning `FootnotesPart` / `EndnotesPart` (#457); the #268 completeness signal trips when the PDF lattice path drops an out-of-grid fragment (#450 follow-up); multi-level chart category axes and blank leaf category labels (#321).
- Angular: serialize document-list `fieldFilters` into bindable query params (#415); dark-theme-aware document-detail cabinet text color.

### Removed

- **The static MCP `X-Api-Key` authentication channel** (added in 0.2.0, hardened in #431–#435) — `/mcp` is OAuth-only again. Guided OAuth and the OAuth client-credentials grant cover both interactive and headless clients, so a standing pre-shared secret is redundant; a leftover `Mcp:ApiKey` configuration section is now inert and ignored (#514).
- The export-template layer, superseded by the document-list-driven "Data Download" export (#499); unused lerna tooling from the build (#503).

### Security

- The gitignored `appsettings.secrets.json` no longer leaks into `dotnet publish` output (#502).
- Within the line, the MCP API-key channel was first promoted to a real ASP.NET Core authentication scheme and hardened — per-IP rate limiting, SHA-256 hash-at-rest keys, and a least-privilege service-account seed (#431, #433, #434, #435) — and then retired in favour of OAuth-only `/mcp` (#514, see Removed); the granular history is in the preview sections below.

## [0.3.0-preview.4] - 2026-07-13

Fourth preview of the 0.3.0 line. Headline work: tenant administration is enabled in the host application, document pipeline background jobs now preserve the tenant context they were scheduled for, and the tenant-scoped MCP URI surface has been tightened after review. As a `0.y.z` pre-release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Added

- **Tenant Management host module** — the host app now includes ABP Tenant Management, its EF Core mapping / migration, and Angular routes so host operators can administer tenants from the deployed host UI (#522).
- **Pipeline job tenant-context persistence tests** — regression coverage now guards that document pipeline jobs keep the tenant context they were scheduled with, including follow-on reprocessing dispatch paths (#521).

### Fixed

- **Background-job tenant context** — parse, classification, field extraction, segmentation, and cabinet-suggestion jobs now execute in the document's tenant context instead of leaking through ambient or missing tenant state (#521).
- **Tenant-scoped MCP URI cleanup** — explicit-tenant resource URI helpers now use shared constants and normalized formatting so document, document-type, and cabinet links stay consistent across tools and resources (#519 follow-up).

## [0.3.0-preview.3] - 2026-07-13

Third preview of the 0.3.0 line. Headline work: the MCP egress can now carry an explicit tenant scope across tools and resource URIs, so host-side operators and automation can stay in the selected layer without relying on ambient context alone. As a `0.y.z` pre-release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Added

- **Tenant-scoped MCP reads** — `search_documents`, `get_document`, `list_document_types`, and `list_cabinets` accept an optional `tenantId` and return tenant-scoped resource URIs when supplied. Document, document-type, and cabinet resources now also expose explicit-tenant URI templates, preserving the selected tenant as clients follow MCP links.
- **MCP tenant-scope contract tests** — tool-schema and resource-template tests guard that `tenantId` stays visible to MCP clients while the internal service provider parameter remains hidden.

## [0.3.0-preview.2] - 2026-07-12

Second preview of the 0.3.0 line. Headline work: the MCP egress returns to **OAuth-only** (the static `X-Api-Key` channel is removed); a new **`PendingReview`** lifecycle status separates "blocked on a review reason" from "still processing"; and the document-type **configuration-pack** import/export flow is now driven from the operator UI. As a `0.y.z` pre-release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Added

- **`DocumentLifecycleStatus.PendingReview`** — a document whose pipelines have run as far as they can but that still carries a blocking review reason (`UnresolvedClassification` / `DuplicateSuspected` / `FieldExtractionIncomplete`) now derives to `PendingReview` instead of falling back to `Processing`, giving the operator UI an honest, non-spinner status. The egress gate is untouched — only the transition to `Ready` fires `DocumentReadyEto`, so `PendingReview` withholds downstream release exactly as `Processing` did; document statistics gain a matching `PendingReviewCount` bucket (#510).
- **Document-type configuration-pack import/export UI** — the #444 config-pack backend (export a document type with its field definitions as a portable pack; import into another layer or deployment) is now driven from the operator Angular app: "Export All" / per-type export, and an import modal with local shape validation, a preview of the types and field counts, create-and-update / create-only reconciliation, and a created / updated / skipped result panel (#444, #513).

### Removed

- **The static MCP `X-Api-Key` authentication channel** (added in 0.2.0 via #428, hardened in #431–#435) — `/mcp` is OAuth-only again. Both Claude and ChatGPT / OpenAI Codex now complete Guided OAuth with the pre-registered `client_id` (#281), and a headless / service client uses the OAuth client-credentials grant, so a standing pre-shared secret is redundant — building it had reinvented a machine credential OpenIddict already ships. The `/mcp` IP rate limiter (#433) and the #278 OAuth discovery are unaffected; a leftover `Mcp:ApiKey` configuration section is now inert and ignored (#514).

## [0.3.0-preview.1] - 2026-07-11

First preview of the 0.3.0 line, opening the post-0.2.0 development cycle. Headline work: broadened ingestion (digital upload formats) and deeper OpenXML / PDF structure extraction; the document-list-driven **Data Download** export with extracted-field filtering; portable document-type **packs**; and two MCP fronts — a downstream-extensible surface (#475) and a size ceiling on every text crossing an LLM boundary (#491). As a `0.y.z` pre-release the exit contracts may still change — see [CONTRIBUTING → Versioning and releases](CONTRIBUTING.md#versioning-and-releases).

### Added

- **Digital upload formats** — accept CSV / TSV, DOCX, plain-text, and XLSX uploads through the same pipeline as scans and PDFs (#471).
- **The MCP surface is now additively extensible by downstream modules** — a downstream ABP module (e.g. a commercial edition layered on the channel) appends its own tools via `AddMcpServer().WithTools<T>()` and its own `resources/list` categories via `VaultExtractMcpOptions.ResourceListContributors` (`IMcpResourceListContributor`), without forking. The built-in document-type and cabinet categories become the first two contributors, and fail-closed grant semantics are preserved bit-for-bit. The open-source surface stays strictly single-tenant — cross-tenant capability, if any, lives entirely in downstream editions with per-call authorization (#475, #476).
- **MCP cabinet discovery and cabinet-scoped search** — cabinets are exposed as a discovery resource and `search_documents` can scope to one (#473).
- **MCP `search_documents` result truncation is explicit** — the response carries `totalCount` / `truncated`, at parity with `list_document_types` (#445).
- **Document-type configuration packs** — export a document type with its field definitions as a portable "pack" and import it into another layer or deployment (#444).
- **Field-definition prompts accept Markdown with an AI-polish action** — a Markdown prompt editor and an AI "polish" endpoint; the former prompt length cap is dropped (#447).
- **"Data Download" export surface** — the export flow is repositioned onto the document list, defaults to all of the type's fields, filters by extracted field values, and adds an "export current view" toolbar action (#414, #496).
- **Extracted-field-value filter on the operator document list** (#415).
- **A full ABP integration test for the MCP API-key channel** — a key-authenticated service-account principal resolves permissions through the real ABP permission checker (granted → search returns rows; ungranted → fail-closed `AbpAuthorizationException`) (#432).

### Changed

- **A size ceiling now bounds every text crossing an LLM boundary** (#491). Field extraction and segmentation **gate** above the ceiling — where the sought value can sit anywhere, the job reaches a terminal review state (`FieldExtractionIncomplete` / `SegmentationIncomplete`) rather than silently extracting from a truncated prefix, and never rethrows an oversized body into the job-retry loop; the decline is surfaced to LLM-facing readers. Classification / title / cabinet suggestion and the MCP document body **truncate** surrogate-safely and announce the cut. Prompt ceilings are host configuration (`VaultExtractBehaviorOptions`); the MCP egress ceiling is a compile-time `const`.
- **One figure format across PDF and OpenXML** — OpenXML figure transcriptions are wrapped in the same `ImageOcrMarkup` markers the PDF path emits, so downstream sees a single figure representation (#480).
- **Deeper OpenXML structure extraction** — DOCX heading levels resolved from custom styles (basedOn chain + style `outlineLvl`, with explicit `outlineLvl=9` cancelling a heading) (#316); DOCX footnotes / endnotes surfaced in Markdown (#315); DOCX figures walked per picture instance for grouped multi-image + text-box caption precision (#322); PPTX group-transform / layout-inherited offsets and group ext/chExt scale composed into reading order (#313, #456); `mc:AlternateContent` PPTX shapes no longer silently skipped (#319). Custom-style heading resolution and DOCX figure traversal are memoized per document (#458, #318).
- **Build / packaging** — SourceLink, deterministic CI builds, and NuGet symbol packages; NuGet and npm license metadata aligned with the LGPL-3.0-only LICENSE.

### Fixed

- Non-BOM legacy-encoded `.csv` / `.tsv` / `.txt` no longer decode as UTF-8 and land in `Document.Markdown` as U+FFFD garbage (#493).
- Restore the embedded-document route that the segmentation rework left detected-but-unroutable — as a Markdown slice (#494).
- Restore the sub-document delete guard on both delete paths (#508).
- DOCX note-body hyperlinks resolved against the owning `FootnotesPart` / `EndnotesPart` (#457); the #268 completeness signal trips when the PDF lattice path drops an out-of-grid fragment (#450 follow-up); multi-level chart category axes and blank leaf category labels (#321).
- Angular: serialize document-list `fieldFilters` into bindable query params (#415); dark-theme-aware document-detail cabinet text color.

### Removed

- The export-template layer, superseded by the document-list-driven "Data Download" export (#499).
- Unused lerna tooling from the build (#503).

### Security

- **The MCP API-key channel is now a real authentication scheme** (#431), replacing the path-scoped middleware. A valid key authenticates via an ASP.NET Core `AuthenticationHandler` (engaged by the cookie `ForwardDefaultSelector`, keeping the endpoint's bare scheme-free `RequireAuthorization()`), so its principal flows through ABP `UseDynamicClaims` — **disabling or deleting the mapped service-account user now revokes the key on the next request**, at parity with a Bearer user (previously revocation was config-removal-only).
- **MCP API-key channel hardening** (follow-ups to #428 / #430): the `/mcp` endpoint is now **rate-limited** per client IP — covering both the API-key channel and the OAuth discovery `401` path — and a present-but-invalid key raises a rate-limited security `Warning` (source IP + header name, never the value) (#433). API keys can be configured as a **SHA-256 `KeyHash`** (hash-at-rest) instead of plaintext, so a config/secret-store leak no longer exposes usable keys (#435). An opt-in host seed (`Mcp:ApiKey:SeedServiceAccounts`) **enforces least privilege** on each configured service account — applying exactly the `VaultExtract.Documents` grant and failing startup if the account is missing, over-privileged, or holds any role (#434).
- The gitignored `appsettings.secrets.json` no longer leaks into `dotnet publish` output (#502).

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

[Unreleased]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...v0.3.0
[0.3.0-preview.4]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.3...v0.3.0-preview.4
[0.3.0-preview.3]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.2...v0.3.0-preview.3
[0.3.0-preview.2]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.1...v0.3.0-preview.2
[0.3.0-preview.1]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...v0.3.0-preview.1
[0.2.0]: https://github.com/dignite-projects/vault-extract/compare/v0.1.0...v0.2.0

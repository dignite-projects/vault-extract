# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Per-document-type upload permissions** ([#629](https://github.com/dignite-projects/vault-extract/issues/629)), built on ABP's resource-based authorization rather than a hand-written grant table. Two names are introduced: the standard permission `VaultExtract.DocumentTypes.ManagePermissions`, which gates who may hand out access, and the resource permission `Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Upload`, granted per document type to a user or a role. A caller's type scope for upload is now "every type of the layer if they hold `Documents.ConfirmClassification`, otherwise the types they hold the `Upload` grant on". Grants live in `AbpResourcePermissionGrants`, which has existed since the `Initial` migration — **no schema change and no EF migration is required**. `DocumentTypeDto` gains a `resourcePermissions` dictionary reporting the caller's own grants, and `IDocumentTypeAppService.GetVisibleAsync` additionally admits `Documents.Upload` holders so an upload-only caller can see the list to pick from. Managing the grants goes through ABP's own resource-permission endpoints and dialog; nothing is added to `IDocumentTypeAppService`. See [the configuration page](docs/en/configuration/document-type-upload-permissions.md). This release carries the backend half; the operator-UI half follows separately.

  > **Both new strings are frozen contracts from the first grant row onwards**, the same discipline the `Extract:*` error codes follow — they are persisted verbatim in the grant table. The resource name must in particular stay equal to `typeof(DocumentType).FullName`, which a unit test asserts.

### Changed

- **BREAKING — an upload with no `DocumentTypeId` now requires `VaultExtract.Documents.ConfirmClassification`** ([#629](https://github.com/dignite-projects/vault-extract/issues/629)). Previously any `Documents.Upload` holder could upload untyped and let LLM classification assign the type. Leaving that open would make the new per-type grant trivially bypassable: upload untyped, let the classifier land the document in a type the caller was never granted, and it still reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)`.

  > **Migration:** any role that holds `Documents.Upload` **without** `Documents.ConfirmClassification` and relies on untyped upload must be granted `Documents.ConfirmClassification`, or its uploads will start failing with `AbpAuthorizationException`. Roles that already hold both are unaffected, as are callers that always supply `DocumentTypeId`.

## [0.5.0-preview.4] - 2026-09-04

Re-cuts `0.5.0-preview.3`, whose release run pushed the NuGet packages to GitHub Packages and then failed on the npm step, leaving no npm package and no GitHub Release. The content below is preview.3's, plus the workflow fix. **Nothing consumes `0.5.0-preview.3`; use this version.**

Closes out the field architecture v3 track opened in `0.5.0-preview.1`: the v2 storage is removed, and the four query guarantees the v3 switch had silently dropped are restored and tested on the live path. The Angular library is renamed to `@dignite/ng.vault-extract`.

### Changed

- **BREAKING — field architecture v2 storage is removed** ([#593](https://github.com/dignite-projects/vault-extract/issues/593), closing [#558](https://github.com/dignite-projects/vault-extract/issues/558)). The `VaultFieldDefinitions` and `VaultDocumentExtractedFields` tables are dropped, together with `FieldDefinition`, `DocumentExtractedField`, `IFieldDefinitionRepository`, and the one-shot `FieldArchitectureV3Migrator` that existed only to read them.

  > **Upgrading to this release requires the v3 data migration to have completed first.** The migrator reads the v2 tables; once they are gone there is no path from v2 data, and the failure is silent — a consumer that generates its own EF migration gets one containing both the v2 `DROP TABLE` and the v3 `CREATE TABLE`, applies it, and every field value is lost while the upgrade reports success. A deployment still on v2 must first take a `0.5.0-preview.1` or `0.5.0-preview.2` build, where v2 and v3 coexist, run the migration and verify it (see [the runbook](docs/en/deployment/field-architecture-v3-migration.md)), and only then move to this release. After this release the only rollback is a database restore.

- **BREAKING — the Angular library is renamed `@dignite/vault-extract` → `@dignite/ng.vault-extract`**, matching the `@dignite/ng.*` convention the sibling Angular packages already follow (`@dignite/ng.flex-fields`, `@dignite/ng.flex-fields-ckeditor`). The sub-entry-points move with it (`@dignite/ng.vault-extract/documents`, `/config`), as does the pre-release GitHub Packages name (`@dignite-projects/ng.vault-extract`). Consumers must update their `package.json` and every import site; `@dignite/vault-extract` on npmjs stops at `0.3.2` and receives no further releases.
- **`@dignite/ng.flex-fields` / `@dignite/ng.flex-fields-ckeditor` and `Dignite.Abp.FlexFields.*` bumped to `10.0.0-rc.14`**, in lockstep across both sides. rc.14 is what aligns the adapter package's range on its intra-repo sibling: `@dignite/ng.flex-fields-ckeditor` pinned `@dignite/ng.flex-fields` at a stale `^10.0.0-rc.4`, which let npm install a second, nested copy — two Angular DI identities for the module-scoped `FLEX_FIELD_TYPES` token, so field types registered against one copy silently failed to resolve from the other at runtime. The npm workspace alias to GitHub Packages (`npm:@dignite-projects/...`) is dropped now that the real `@dignite/*` packages are live on npmjs, closing the npmjs-availability blocker from [#577](https://github.com/dignite-projects/vault-extract/issues/577).
- **The npm install no longer needs a registry credential.** With the alias gone, every dependency resolves from npmjs, so the `@dignite-projects` scope mapping and its `${GITHUB_TOKEN}` placeholder are removed from `angular/.npmrc` and the `PACKAGES_READ_TOKEN` drops out of both workflows' `npm ci`. The npm counterpart of [#595](https://github.com/dignite-projects/vault-extract/pull/595), which did this for the .NET restore. Publishing a pre-release to GitHub Packages still authenticates, in the publish step's own config.
- **The dependency stack moves forward as a batch**: OpenTelemetry 1.13/1.15 → 1.18, EF Core tooling 10.0.2 → 10.0.11, `Microsoft.Extensions.AI` 10.6 → 10.9, `Microsoft.Agents.AI` 1.8 → 1.20, plus Fody, Source Link, Serilog, Markdig and the test stack (Test SDK 17.14 → 18.9, `xunit.runner.visualstudio` 3.1 → 4.0, NSubstitute 5.3 → 6.2). Three coupled groups are held back behind their own issues, because nothing a build or unit test reaches speaks to them: the MCP egress major (`ModelContextProtocol.AspNetCore` 1.3.0 → 2.2.0 — protocol and transport behaviour), text extraction (`MarkItDotNet` and the three packages pinned to the exact versions it pulls transitively, which only mean anything moved together), and PDF rasterisation (`PDFtoImage` 5.4.0 resolves SkiaSharp 4.150.1, not the 4.151.2 native assets it was paired with).
- **The unreferenced `ModelContextProtocol` central-package entry is removed.** Only `ModelContextProtocol.AspNetCore` is referenced anywhere in the repository, and it pulls `ModelContextProtocol.Core` transitively; the dead entry is what made a coupled-looking version pair split.

### Added

- **A build guard that fails when the published bundle imports a package the published `package.json` does not declare** ([#577](https://github.com/dignite-projects/vault-extract/issues/577)). ng-packagr externalises every bare specifier it does not bundle but never checks that the externals are declared, so this class of defect is silent through build, `npm install`, and CI — it only surfaces when a consumer builds their own app. Runs in CI and as part of `npm run build:vault-extract`. Ported from the sibling `abp-modules` repo; it is what found the `@abp/ng.components` omission below.
- **A deployment runbook for the field architecture v2 → v3 data migration** ([#561](https://github.com/dignite-projects/vault-extract/issues/561)), covering what the migrator is (module-level, additive, idempotent, one tenant layer per call), who invokes it, the measure / apply / migrate / verify sequence, four verification queries, and the rollback boundary. Required reading before taking this release — see the BREAKING entry above.

### Fixed

- **The pre-release npm publish failed on the dist-tag.** npm/cli rejects a prerelease version whose dist-tag is left at its default — the check is on whether `--tag` was passed at all, not on its value — so the release workflow's GitHub Packages step now passes `--tag latest` explicitly. `latest` is correct for that registry: it only ever receives pre-releases (stable goes to npmjs under the public `@dignite/*` name), so it tracks the newest preview and an unpinned install keeps resolving. The step was green through `0.5.0-preview.2` because the npm it installs has since picked up the check.
- **Field queries lost four of v2's fail-closed guards in the v3 switch** ([#593](https://github.com/dignite-projects/vault-extract/issues/593)). `DocumentFieldQueryResolver` + `DocumentFlexFieldQueryExecutor` replaced `IDocumentRepository.GetFieldMatchedIdsAsync` without carrying over the rejections for a range filter on an unordered field type, a value that does not parse as its declared type, an offset-bearing `DateTime`, and a wholly empty filter. v2's integration tests kept passing throughout because they exercise `GetFieldMatchedIdsAsync` directly, which nothing on the live path calls — vouching for dead code while the path that runs went untested. All four guards are restored, expressed on `FlexFieldValueType` rather than a per-field-type switch, and the cases are ported onto the v3 path; each guard was mutation-tested before landing.
- **The Angular library did not declare `@abp/ng.components`**, which its bundle imports (`@abp/ng.components/extensible`) in five components. It is not reachable transitively: `@abp/ng.components` peer-requires `ng.core` / `ng.theme.shared` rather than being pulled in by them, so a consumer without their own direct dependency on it got an unresolvable specifier. The same shape as the `@dignite/ng.flex-fields` omission that opened #577.
- **The library declared no Angular peer dependencies at all.** `@angular/common` / `core` / `forms` / `platform-browser` / `router`, `rxjs`, `@ng-bootstrap/ng-bootstrap` and `@ngx-validate/core` are now declared as peers, stating the versions the library is actually built against instead of leaving a consumer to infer them.

## [0.5.0-preview.3] - 2026-09-04

**Partially published — do not use.** The release run pushed the NuGet packages to GitHub Packages and then failed publishing the npm package, so this version exists on one channel only and has no GitHub Release. Superseded by [0.5.0-preview.4], which carries the same changes plus the fix.

## [0.5.0-preview.2] - 2026-09-03

Re-cuts `0.5.0-preview.1` from a tagged commit. That preview was published by two separate `workflow_dispatch` runs, so its artifacts do not correspond to a single commit — the NuGet packages came from `e9f86ca0`, the npm package from `9aeac63d`, after the publish-reporting fix below. It also predates the Angular ABP upgrade, so the published library declared `@abp/ng.core: ~10.2.0` while the .NET stack was already on 10.5.0; a consumer could only reconcile that with an `overrides` entry, which suppresses the mismatch rather than resolving it. **Consumers pinning `0.5.0-preview.1` should move to this version.**

### Changed

- **The Angular ABP packages upgraded from 10.2.0 to 10.5.0** (#590), closing the last of [#558](https://github.com/dignite-projects/vault-extract/issues/558)'s gates. The .NET side moved with field architecture v3; the Angular runtime stayed behind, below the `~10.5.0` peer range `@dignite/ng.flex-fields` declares. `@abp/ng.theme.lepton-x` moves to `~5.5.0` on its own version line. `legacy-peer-deps` stays: `@abp/ng.theme.shared` still pins `@swimlane/ngx-datatable@~22.0.0`, whose peer range stops at Angular 20 while this workspace is on 21 — a conflict upstream of ABP that no ABP version fixes.
- **The Angular container image packages the prebuilt SPA** instead of building it in the container ([#584](https://github.com/dignite-projects/vault-extract/issues/584)). `docker-compose` points at `Dockerfile.local`, which is what `run-docker.ps1` already did; the container-side `npm ci` and with it the GitHub Packages credential requirement are gone, and the now-unused `angular/Dockerfile` is removed.

### Fixed

- **The npm publish step reported success on a failed publish.** It piped `npm publish` through `tee` under a shell with no `pipefail`, so the step's status was always `tee`'s. That is how a green run had in fact returned `E401`.

## [0.5.0-preview.1] - 2026-09-02

Two structural changes land together in this preview: **field architecture v3**, which replaces the hand-rolled field storage with the `Dignite.Abp.FlexFields` kernel, and the **Angular workspace migration** from Nx back to the Angular CLI. Both are breaking, which is why this opens a `0.5.0` line rather than continuing `0.4.x`.

> **This preview publishes to GitHub Packages only.** A stable release additionally requires `@dignite/ng.flex-fields` to exist on npmjs, which it does not yet — see [#577](https://github.com/dignite-projects/vault-extract/issues/577). Consuming the preview npm package means adding the `@dignite-projects` scope to your `.npmrc`.

> **Upgrading is breaking on three axes**: the field storage schema (a data migration ships with it), the Angular library's own dependencies, and the workspace layout if you build from source. See the Changed entries below.

### Added

- **Operator correction of extracted Markdown** — an operator can now fix small OCR/parsing errors directly on a document, with an optional unified reprocess toggle that re-runs the downstream pipeline stages against the corrected text. Corrections are tracked through ABP's audit log rather than a separate history table (#555).
- **`Tags` field type** — Vault Extract's own open-vocabulary multi-value field type, replacing v2's `AllowMultiple` flag on a text field. Multi-value ordering is preserved by the JSON array itself rather than a separate `Order` column.
- **`Select` options projected into the LLM extraction schema as an `enum`** — closed-vocabulary fields now constrain model output structurally instead of relying on free-text prompt instructions.
- **`Month` as a third `DateTime` input mode**, alongside date and date-time.
- **Field-type metadata served to the client** — the Angular field designer no longer hardcodes which field types can be marked searchable; the server declares it, closing two searchability gaps where the UI and the server disagreed.
- **Pre-release Angular packages now publish to GitHub Packages** as `@dignite-projects/vault-extract`, mirroring the pre-release NuGet channel. Previously pre-release UI builds were compiled and then published nowhere (#577).

### Changed

- **BREAKING — field architecture v3: `FieldDefinition` + `DocumentExtractedField` replaced by the `Dignite.Abp.FlexFields` kernel** (#558, #559, #562). Field values now live in a JSON value bag on `Document` with a derived, rebuildable index table, instead of typed child rows. Field definitions become `Field : IFlexField`, carrying `FieldTypeName` + `Configuration` in place of the `FieldDataType` enum; `Prompt` becomes `Description`. The egress contract is unchanged — `ExtractedFields`, `FieldsExtractedEto` and the REST/MCP surfaces keep their shape. A row-level data migration ships with this release and runs from inside the module, so any host embedding the EF model runs the same code (#561).
- **BREAKING — the Angular field UI is rebuilt on `@dignite/ng.flex-fields`** generic host components, with a field-type picker and a per-type configuration panel. The library now declares `@dignite/ng.flex-fields` as a dependency, which consumers must be able to resolve.
- **BREAKING — the Angular workspace moved from Nx back to the Angular CLI** (#579). `angular.json` replaces `nx.json` and the per-project `project.json` files; the host app moved to `src/` at the workspace root and `apps/` is gone; the library moved from `packages/` to `projects/`. This matches every sibling Angular project. Proxy generation returns to the standard `abp generate-proxy -t ng`, and `@abp/nx.generators` is dropped. Only affects building from source — the published package's shape is unchanged.
- **ABP upgraded from 10.2.0 to 10.5.0** across the .NET stack, which the flex-fields kernel requires.
- **Per-field-type behaviour consolidated into an extensible registry** (#564), replacing four independent string ladders that each had to be updated when a field type was added. The founding commit had already missed one of them, silently disabling duplicate detection for `Select` fields marked as a unique key.

### Fixed

- **`release.yml` never authenticated against GitHub Packages** — the .NET restore passed the built-in `GITHUB_TOKEN`, which grants `packages:read` only for packages published by this repository, and the `npm ci` step had no credential at all. The first release attempt after the flex-fields dependency landed would have failed `E401`. Commit `4309740c` had fixed exactly this in `ci.yml` and did not reach `release.yml`.
- **Deployment scripts broken by the Nx removal** — `run-docker.ps1` still invoked `npx nx build host` after Nx was uninstalled, so the documented deployment command failed at the frontend prebuild. `build-images-locally.ps1` also called an `npm run build:prod` script that has never existed in this repository.
- **The Angular library did not declare its flex-fields dependency** — the built bundle emitted a bare `@dignite/ng.flex-fields` import with nothing in the package metadata to resolve it (#577).
- **v3 migration correctness**, found by running it against a real database rather than only under test: the migrator now opens its own unit of work, always rebuilds the derived index (an "already migrated" shortcut could skip the one step that had not finished while reporting success), and iterates tenants so a host-only pass no longer leaves tenant-layer data on v2. Soft-deleted v2 field definitions are migrated too.
- **Field-rename value-bag migration scoped to its own document type**, instead of touching same-named fields under other types.
- **A permission gap and a v1 document-type-pack import failure** surfaced while cutting the admin surface over to v3.
- **GitHub Packages auth wired into the host Docker build**, and a real PAT used for cross-repo package reads in CI.

### Security

- **MCP tenant-scoped reads gated behind a fail-closed admission seam** (#524). An explicit `tenantId` on an MCP read is now checked against an admission seam that denies by default, closing a path where a caller could address another tenant's documents. Present in the shipped 0.3.0–0.3.2 line; tracked publicly as GHSA-x36r-v84w-cg8h.

## [0.3.2] - 2026-08-23

Patch release for the 0.3.x stable line. This release closes a silent field-extraction truncation on large multi-value fields (a production bank-statement document lost the tail of a transcribed table with no error signal), and fixes a cluster of vision-LLM OCR and PDF ruling-line issues surfaced by the same document — LaTeX-table / layout-annotation cleanup, no-content-refusal normalization, and stacked per-row table-box detection.

### Fixed

- **Field extraction silently truncating large multi-value fields** — `FieldExtractionWorkflow` never set `ChatOptions.MaxOutputTokens`, so SiliconFlow silently applied its own ~4096-token default completion cap; even once sized (CJK-aware, since field values preserve the document's original wording and `DefaultLanguage` defaults to Japanese), the OpenAI SDK serializes it as `max_completion_tokens`, which SiliconFlow's endpoint doesn't recognize and silently ignores instead of erroring. A `DelegatingHandler` on the host's SiliconFlow `HttpClient` now rewrites the outgoing request body to the legacy `max_tokens` field it does understand; a response cut off at the limit (`FinishReason.Length`) also now logs a distinct warning instead of degrading with no diagnosable signal.
- **Chat-client HTTP hangs from stale pooled connections** — the host's OpenAI-compatible `HttpClient` now bounds `PooledConnectionLifetime` to 5 minutes and raises `NetworkTimeout` to 300s (with the SDK's own 100s `HttpClient.Timeout` disabled, since it was silently overriding `NetworkTimeout` back down to 100s), closing a failure mode where a NAT/proxy-dropped idle connection hung every request that reused it for the full timeout before ABP's retry loop re-queued the job.
- **VisionLlm OCR: LaTeX tables and leaked layout annotations** — a vision LLM (observed on Qwen3-VL), trained heavily on academic document datasets, still rendered line-item tables as `\begin{tabular}` and occasionally leaked an internal bounding-box HTML comment into the transcription, despite the prompt requiring GitHub-Flavored Markdown and forbidding both. `VisionLlmOutputGuard` now deterministically rewrites `\begin{tabular}` blocks to GFM tables and strips HTML comments, the same fix pattern as the existing code-fence stripper (#448).
- **VisionLlm OCR: single-page running-header/footer judgment** — the prompt now asks the model to judge boilerplate by position and style rather than by confirmed repetition (a single-page call can never confirm repetition across pages), closing a case where a page-bottom confidentiality line and page number survived transcription.
- **VisionLlm OCR: no-content refusals inlined as transcribed text** — despite the prompt asking for no output at all on an unreadable image, the model sometimes returned an English refusal sentence instead, which was then treated as real content and occasionally misjudged by sub-document segmentation as a standalone document (observed on two purely decorative images in a bank-statement PDF). `VisionLlmOutputGuard.LooksLikeNoContentRefusal` now normalizes a detected refusal to an empty result, length-gated so real (longer) transcriptions can never be misclassified; the segmentation prompt also now treats a confirmed-empty embedded-image OCR result as never a standalone document.
- **PDF ruling-line grid detection missed stacked per-row table boxes** — some report engines (observed on a Japanese bank-statement PDF) draw a table's grid as many small per-row rectangles rather than continuous ruling lines, which `DetectGrids` silently dropped, falling back to the less reliable whitespace-heuristic path and mis-clustering rows. `ChainStackedRowBoxes` / `ChainCollinearVerticalSegments` chain these leftover shapes into valid rule candidates.

### Changed

- **`SlugSuggestionAppService` moved off the `Structured` keyed chat client onto `TitleGenerator`** — `Structured` is now reserved for classification and field extraction, the accuracy-sensitive callers where mis-output is user-visible, so it stays pinned to a strong model rather than trimming cost; slug suggestion's single-shot, prompt-unique-per-call shape matches `TitleGenerator`'s existing usage instead.

## [0.3.1] - 2026-07-17

Patch release for the 0.3.x stable line. This release tightens field-schema cleanup and prompt-budget guards, hardens MCP client-IP handling behind trusted proxies, fixes delete guards around recycle-bin documents, restores stable NuGet symbol validation by embedding PDBs, and adds operator bulk-delete selection to the document list and recycle bin.

### Added

- **Bulk document delete selection** — the operator document list and recycle bin now support multi-row selection and bulk delete actions, with localized selection/status text (#534).

### Changed

- **Release packaging** — NuGet packages now embed portable PDBs in the assemblies instead of shipping separate `.snupkg` symbol packages, avoiding NuGet.org symbol-validation failures while preserving Source Link step-in support.

### Fixed

- **Field-definition cleanup** — deleting field definitions now removes orphaned field-validation warnings and duplicate-basis cleanup is schema-aware, so fields with matching codes in different document types are not crossed (#528).
- **Field-schema prompt budgets** — schema prompt-budget validation now applies per document type and accounts for the actual cascade restore fields (#468).
- **Cabinet and document-type deletion** — cabinet deletion unfiles recycle-bin documents and uses set-based cleanup; document-type delete guards now count recycle-bin documents instead of allowing a type with retained documents to be removed (#530, #531).

### Security

- **MCP forwarded client IP handling** — the host now trusts forwarded client IPs only through explicitly configured proxies and normalizes trusted proxy address forms, keeping rate-limit identity stable behind supported reverse proxies (#469).

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

[Unreleased]: https://github.com/dignite-projects/vault-extract/compare/v0.5.0-preview.4...HEAD
[0.5.0-preview.4]: https://github.com/dignite-projects/vault-extract/compare/v0.5.0-preview.3...v0.5.0-preview.4
[0.5.0-preview.3]: https://github.com/dignite-projects/vault-extract/compare/v0.5.0-preview.2...v0.5.0-preview.3
[0.5.0-preview.2]: https://github.com/dignite-projects/vault-extract/compare/v0.5.0-preview.1...v0.5.0-preview.2
[0.5.0-preview.1]: https://github.com/dignite-projects/vault-extract/compare/v0.3.2...v0.5.0-preview.1
[0.3.2]: https://github.com/dignite-projects/vault-extract/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...v0.3.0
[0.3.0-preview.4]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.3...v0.3.0-preview.4
[0.3.0-preview.3]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.2...v0.3.0-preview.3
[0.3.0-preview.2]: https://github.com/dignite-projects/vault-extract/compare/v0.3.0-preview.1...v0.3.0-preview.2
[0.3.0-preview.1]: https://github.com/dignite-projects/vault-extract/compare/v0.2.0...v0.3.0-preview.1
[0.2.0]: https://github.com/dignite-projects/vault-extract/compare/v0.1.0...v0.2.0

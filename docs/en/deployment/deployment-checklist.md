# Deployment / Release Smoke Test Checklist

This file collects recurring verification items that should be re-run when deploying to a new environment, upgrading critical dependencies, or shipping changes that affect the core pipeline.

Items here are not gated by GitHub Issue status. They live alongside the deployable artifact and are re-run per release. When a feature ships with end-to-end verification that cannot be automated yet, copy its checks into a new section here, tagged with the originating issue.

**How to use**: when cutting a release, copy the relevant section(s) into the release ticket and tick boxes as you verify. When a check graduates into automation (CI, smoke test job, etc.), remove it from this file with a note in the commit.

---

## PaddleOCR PP-StructureV3 sidecar (#80)

Verifies the default OCR provider after a sidecar upgrade, model swap, or fresh-clone bring-up. Run end-to-end through `docker compose up` against real samples — no synthetic fixtures.

### Out-of-the-box bring-up

- [ ] Fresh clone → `docker compose up paddleocr` succeeds; first start downloads ~600 MB of model weights without any external credentials
- [ ] `docker compose up` (full stack) cold-start time recorded; first-run model download bandwidth recorded
- [ ] Upload a scanned document via the host → `Document.Markdown` is populated with non-empty Markdown, no Azure / cloud credentials required

### Markdown output quality

- [ ] Chinese contract scan with seal → `OcrResult.Markdown` contains heading / paragraph / table Markdown markers; seal regions surface as image placeholders
- [ ] Chinese invoice scan → tables rendered as HTML / Markdown tables; amount / date fields read correctly
- [ ] Japanese scan → Markdown output preserved across the language switch (no pipeline error)

### Model variant compatibility

- [ ] `PaddleOcr:ModelName = "PP-OCRv4"` → OCR still runs; `OcrResult.Markdown` contains flat Markdown paragraphs (no native document structure)
- [ ] `PaddleOcr:ModelName = "PaddleOCR-VL-1.5"` (GPU environment only) → OCR still runs; #78 Markdown-output acceptance preserved

### Performance

- [ ] CPU performance baseline: end-to-end latency reported for 1-page and 5–10-page PDFs on a developer-laptop spec; recorded against the previous baseline if any

### Downstream pipeline

- [ ] End-to-end: scan upload → Parse → Classification → Host / tenant field extraction → `DocumentReadyEto` published (verify the published Markdown preserves heading / table structure so downstream RAG consumers can chunk on `## ` / `### ` markers, not arbitrary character offsets)

### Provider switch-back

- [ ] Switch back to Azure DI by uncommenting `ExtractAzureDocumentIntelligenceModule` in `ExtractHostModule` + matching `ProjectReference` in `host/src/Dignite.Vault.Extract.Host.csproj` + restoring the `AzureDocumentIntelligence` config block → cloud OCR path still passes acceptance

---

## Field architecture v2 — Host startup seed removed

Verifies upgrade + new-deployment behavior after `HostDocumentTypeDataSeedContributor` / `DocumentTypeOptions` / startup-time `host.general` registration were removed. From this version onward `DocumentType` and `FieldDefinition` are managed **exclusively at runtime** via `IDocumentTypeAppService` / `IFieldDefinitionAppService`.

CLAUDE.md's "Document type system (two independent single layers)" section enforces **strict per-layer isolation** (Host docs match `TenantId IS NULL` types; tenant docs match `TenantId = current` types). There is no cross-layer union and no fallback type.

### Fresh deployment bring-up (new environment, empty DB)

- [ ] After first `dotnet run --project host/src/Dignite.Vault.Extract.Host.DbMigrator` (or `dotnet ef database update`), `VaultDocumentTypes` is **empty** — no automatic seed
- [ ] Attempting to upload a document before any `DocumentType` exists in the current scope (`CurrentTenant.Id`) throws `BusinessException(Extract:NoDocumentTypesConfigured)`; document is **not** persisted to the DB nor written to blob storage
- [ ] Host admin (`CurrentTenant.Id IS NULL`) signs in → `IDocumentTypeAppService.CreateAsync` creates a row with `TenantId = NULL`; subsequent host-scope upload succeeds and classification candidate set is non-empty
- [ ] Tenant admin signs in (different tenant) → `GetVisibleAsync` returns **only that tenant's rows**, not the host rows; tenant must create its own type(s) before tenants can upload
- [ ] Cross-tenant isolation: tenant A admin cannot see / edit / delete tenant B rows or host rows even via direct API calls

### Upgrade from earlier deploy (DB already has historical `host.general` row from old seed)

- [ ] Apply migration — historical `host.general` row remains in `VaultDocumentTypes` (no destructive cleanup); existing host documents whose `Document.DocumentTypeCode = "host.general"` continue to classify correctly because `EnsureRegisteredTypeCodeAsync` still finds the row
- [ ] Host admin can edit / delete the historical `host.general` row through the admin UI; if deleted, host-scope uploads will start hitting `NoDocumentTypesConfigured` until another host type is created
- [ ] Tenant uploads are unaffected by the historical host row (single-layer matching: `Document.TenantId != NULL` never reads host rows)

### Soft-deleted DocumentType / FieldDefinition behavior

- [ ] Deleting a `DocumentType` with active documents → behavior matches `Extract:DocumentTypeInUse` policy (verify expected error code surfaces in admin UI)
- [ ] Restoring a soft-deleted `FieldDefinition` whose parent `DocumentType` is also deleted → `Extract:FieldDefinitionParentTypeMissing` blocks the restore; parent must be restored first
- [ ] After restoring a soft-deleted `DocumentType`, classification candidate set picks it up immediately (no admin restart needed)

---

## Migration safety — large-table index builds + pre-deploy data probes (#225)

Re-run before applying EF Core migrations to a database that already holds production data. **On a fresh / empty DB every check below is a no-op** — apply forward in one pass. Migrations live under `host/src/Migrations/`; the concerns below come from the migration-safety review of the field-architecture-v2 + pipeline-aggregate series.

### Pre-deploy table-size probe

- [ ] Record row counts before applying: `SELECT COUNT(*)` on `VaultDocuments`, `VaultDocumentExtractedFields`, `VaultDocumentPipelineRuns`. If all are small (early deployment) the index-lock and `ALTER COLUMN` concerns below are moot.

### Large-table index builds (offline `CREATE INDEX` holds a schema-modification lock)

Each of these migrations creates an index on a hot channel table; on a large table the default offline build blocks reads/writes on that table until it finishes:

- [ ] `Add_FileOrigin_Indexes` — two indexes on `VaultDocuments` (`FileOrigin_BlobName`, `FileOrigin_ContentHash`)
- [ ] `Limit_DocumentExtractedField_StringValue_Length` — composite index on `VaultDocumentExtractedFields`
- [ ] `Pipelines_AggregateRoot_Split` — rebuilds the unique index on `VaultDocumentPipelineRuns`
- [ ] On a large table + SQL Server Enterprise: rewrite as `CREATE INDEX ... WITH (ONLINE = ON)` in a **new** migration / hand-written `migrationBuilder.Sql` (do **not** edit already-applied migrations). On Standard/Web Edition (no ONLINE): schedule a maintenance window / low-traffic period.

### Lossy / blocking operations

- [ ] `Limit_DocumentExtractedField_StringValue_Length` narrows `StringValue` from `nvarchar(max)` → `nvarchar(256)`. On data with existing values longer than 256, `ALTER COLUMN` **aborts the migration** (fail-fast, not silent truncation). Probe first — `SELECT COUNT(*) FROM VaultDocumentExtractedFields WHERE LEN(StringValue) > 256` must be `0` (the write-side validator already caps new values at 256, so this is historical-data-only risk).
- [ ] Forward-only awareness: `Merge_FieldDataType_Integer_Decimal_Into_Number` and `Add_DocumentExtractedField_Order_And_FieldDefinition_AllowMultiple` have **lossy `Down()`** (the first collapses the Integer/Decimal distinction; the second deletes multi-value rows where `Order <> 0`). Prefer a forward-only rollback strategy in production; do not rely on these `Down()` to restore data.

---

## Operator notifications — live push (#680)

Re-run when deploying to a new environment or changing the proxy in front of the host. The first item only applies if the host sits behind a reverse proxy; see [Reverse proxy](deployment.md#reverse-proxy) in the deployment guide.

- [ ] Sign in to the SPA, open the browser dev tools, Network tab, filter `WS`: the request to `/signalr-hubs/notifications` returns **101 Switching Protocols**. If you see a `text/event-stream` request or a repeating `/signalr-hubs/notifications?id=...` request instead, the proxy is not passing the WebSocket upgrade. The bell still works through that fallback, but it is slower and holds more requests open; fix the proxy.
- [ ] Upload a small `.txt` document: the bell badge appears **without a page refresh** once the document lands in review or becomes Ready.
- [ ] `App:CorsOrigins` contains the SPA origin. Otherwise the browser blocks the SignalR negotiate request and no notification ever shows.
- [ ] Migration `V680_AddNotificationCenter` is applied: `GET /api/notification-center/notifications` returns `200`, not `500`.

### Which install the host has

A host installs the notification packages one of two ways (see [Two ways to install the host](../egress/operator-notifications.md#two-ways-to-install-the-host)). Tick the block that matches the deployment and skip the other. The items above are for the monolith install. In a split deployment, run the WebSocket and CORS items against the notification service and skip the migration item: this host no longer owns the Notification Center tables.

- [ ] **Monolith** (`Dignite.NotificationCenter.*`; `Dignite.Abp.Notifications`, the in-process implementation, comes with it): the host does not also reference `Dignite.Abp.Notifications.Client`. With both installed the host distributes by itself instead of sending its notifications to the notification service, and nothing fails to tell you.
- [ ] **Split deployment** (`Dignite.Abp.Notifications.Client` + `Dignite.Abp.Notifications.EntityFrameworkCore`, or `.MongoDB`): the host references no `Dignite.NotificationCenter.*` package, so `Dignite.Abp.Notifications` is not in its dependency tree either, and its `NotificationCenter` connection string points at the notification service's database.
- [ ] **Split deployment**: the notification service's `NotifDefinitions` table holds the four `VaultExtract.Document.*` notifications after this host's first start. Otherwise the service refuses the publish requests and its event inbox keeps retrying them.
- [ ] **Split deployment**: upload a small `.txt` document; the bell badge appears and `/signalr-hubs/notifications` is served by the notification service (the WebSocket request returns **101 Switching Protocols** from there).
- [ ] **Neither install** (no `Dignite.NotificationCenter.*`, no `.Client`): the host logs a warning for every notification and drops it (`NullNotificationPublisher`). That is correct only for a host that deliberately sends no operator notifications.
- [ ] **Upgrading from `abp-modules` 10.0.0-rc.22 or earlier**: `AbpBackgroundJobs` holds no row whose `JobName` is `Dignite.Abp.Notifications.NotificationDistributionJobArgs`; the job is now named `Dignite.Abp.Notifications.Distribute`, and rows under the old name are not picked up.

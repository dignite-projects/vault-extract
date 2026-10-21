```json
//[doc-seo]
{
    "Description": "Per-release smoke tests for a Dignite Vault Extract deployment: startup, storage, document types, text extraction, migrations, events, MCP and operator notifications."
}
```

# Deployment Checklist

Recurring checks to run when deploying to a new environment, upgrading a critical dependency, or shipping a change to the core pipeline. They cover what automated tests cannot: real providers, real proxies, real data.

**How to use**: when cutting a release, copy the relevant sections into the release ticket and tick the boxes as you verify. When a check moves into automation, remove it here and say so in the commit.

---

## Startup and Configuration

- [ ] The host starts without the "Extract requires an LLM provider" error: `Vault:Extract:Endpoint`, `ApiKey` and `ChatModelId` come from the secret store, not from a committed file
- [ ] With Vision-LLM OCR enabled (the default), `Vault:Extract:VisionOcrModelId` is set and names a vision-capable model
- [ ] `StringEncryption:DefaultPassPhrase` is the value the database was created with (never rotated)
- [ ] The OpenIddict signing certificate and `AuthServer:Authority` are production values; `/.well-known/openid-configuration` answers
- [ ] `App:CorsOrigins` lists the SPA origin (and any browser-based MCP client)
- [ ] `--migrate-database` ran for this release and printed `Migration completed.`
- [ ] No `appsettings.secrets.json` from a developer machine reached the deployment (the host excludes it from `dotnet publish`; check images built another way)

## Original File Storage

- [ ] The BLOB storage for `extract-documents` is durable: in a container deployment, `App_Data/blobs` is a persistent volume, or another BLOB provider is configured ([Deployment](deployment.md#original-file-storage))
- [ ] Upload a document, restart or recreate the host, then download its original file from the detail page: the download succeeds
- [ ] Re-parse that document: it completes (re-parse reads the stored file)
- [ ] Backups cover the database **and** the BLOB storage, taken together

## Document Types (Fresh Deployment)

Vault Extract seeds no document types ([Document Types and Fields](../documents/document-types.md)); the shipped host adds a *Sample Document* type in Development only.

- [ ] On an empty database, `VaultDocumentTypes` is empty after migration (outside Development)
- [ ] Uploading before any type exists in the caller's layer fails with `Extract:NoDocumentTypesConfigured`; nothing is stored
- [ ] A Host admin creates a type; a Host upload then classifies against it
- [ ] A tenant admin sees **only** that tenant's types (`GET /api/vault-extract/document-types`), not the Host's; the tenant creates its own before its users can upload
- [ ] Tenant A's admin cannot see, edit or delete tenant B's or the Host's types, even through direct API calls
- [ ] Deleting a type that documents use fails with `Extract:DocumentTypeInUse`
- [ ] Restoring a field whose type is deleted fails with `Extract:FieldDefinitionParentTypeMissing`; restoring the type first restores its fields
- [ ] A type pack exported from a staging layer imports into the production layer (`CreateOnly`), and the result counts match

## Text Extraction

### Vision-LLM OCR (the shipped default)

- [ ] A phone photo of a receipt → `Document.Markdown` holds the line items and amounts, not empty text
- [ ] A photo over 1.5 megapixels → the log shows one downscale line with the original and new size, and the Markdown is not empty ([#692](https://github.com/dignite-projects/vault-extract/issues/692))
- [ ] A scanned (image-only) PDF of several pages → one transcription per page, joined; a PDF over `VisionLlmOcr:MaxPdfPages` fails loudly instead of dropping pages
- [ ] On Linux: `SkiaSharp.NativeAssets.Linux` is referenced and `libfontconfig1` is installed, so scanned PDF pages rasterize ([Vision-LLM OCR](../text-extraction/ocr-vision-llm.md#linux-deployment))
- [ ] Token usage appears in the telemetry backend (`gen_ai.client.token.usage`)

### PaddleOCR Sidecar (When Enabled)

Run after a sidecar upgrade, a model change, or a fresh bring-up, against real samples.

- [ ] `docker compose up paddleocr` succeeds; the first start downloads about 600 MB of model weights without credentials
- [ ] A scanned document → non-empty Markdown, no cloud credentials involved
- [ ] A Chinese contract scan with a seal → headings, paragraphs and tables in the Markdown
- [ ] A Japanese scan → Markdown preserved, no pipeline error
- [ ] `PaddleOcr:ModelName = "PP-OCRv4"` → flat Markdown paragraphs, still no error
- [ ] CPU latency for a 1-page and a 5–10-page PDF recorded against the previous baseline

### Digital Documents

- [ ] A digital PDF with an embedded image → the image's transcription appears inline in the Markdown
- [ ] A `.pptx` and a `.docx` → structure (headings, lists, tables) preserved; `.pptx` needs the OpenXml provider
- [ ] An `.xlsx` → one Markdown table per non-empty worksheet

### End to End

- [ ] Upload → text extraction → classification → field extraction → `DocumentReadyEto`; the published Markdown keeps its heading and table structure, so downstream chunkers can split on `## ` / `### `

## Database Migrations

Run before applying migrations to a database that already holds production data. On an empty database every check is a no-op.

- [ ] Record row counts first: `VaultDocuments`, `VaultDocumentFlexFieldIndexes`, `VaultDocumentPipelineRuns`, `AbpBackgroundJobs`. Small tables make the next two items moot
- [ ] Read the release's migrations for `CREATE INDEX` on those tables. An offline index build locks the table until it finishes: on SQL Server Enterprise add an `ONLINE = ON` build in a **new** migration (never edit an applied one); otherwise schedule a low-traffic window
- [ ] Prefer forward-only rollback in production; do not rely on a migration's `Down()` to restore data
- [ ] Upgrading from 0.3.x: complete the [field architecture v2 → v3 migration](field-architecture-v3-migration.md) first

## Integration Events

- [ ] With a broker configured, a test consumer receives `VaultExtract.Document.Ready` for an uploaded document, with the right `TenantId` and `DocumentTypeCode`
- [ ] Without a broker (the shipped host), that is expected not to happen outside the host process ([Integration Events](../egress/integration-events.md))
- [ ] After a broker outage, the queued events are delivered once it recovers (transactional outbox)

## MCP Server

- [ ] `https://<host>/mcp` without a token returns `401` with a `WWW-Authenticate` header pointing at `/.well-known/oauth-protected-resource/mcp`, and that document names the production `AuthServer:Authority`
- [ ] The `VaultExtract_Mcp` client's redirect URIs are the ones production needs (by default only the claude.ai callback; [Registered Callbacks](../egress/mcp-server.md#registered-callbacks))
- [ ] An MCP client signs in and `vault_extract_search_documents` returns documents for a user with `Documents` + `Documents.ReadAll`, and nothing for a user with `Documents` alone
- [ ] Behind a reverse proxy, `Mcp:ForwardedClientIp` lists the proxy, so the rate limiter sees client IPs rather than one shared bucket

## Operator Notifications (#680)

Re-run when deploying to a new environment or changing the proxy in front of the host. The first item applies only behind a reverse proxy; see [Reverse Proxy](deployment.md#reverse-proxy).

- [ ] Sign in to the SPA, open the browser dev tools, Network tab, filter `WS`: the request to `/signalr-hubs/notifications` returns **101 Switching Protocols**. A `text/event-stream` request or a repeating `/signalr-hubs/notifications?id=...` request instead means the proxy is not passing the WebSocket upgrade. The bell still works through that fallback, but it is slower and holds more requests open; fix the proxy.
- [ ] Upload a small `.txt` document: the bell badge appears **without a page refresh** once the document lands in review or becomes Ready.
- [ ] `App:CorsOrigins` contains the SPA origin. Otherwise the browser blocks the SignalR negotiate request and no notification ever shows.
- [ ] Migration `V680_AddNotificationCenter` is applied: `GET /api/notification-center/notifications` returns `200`, not `500`.

### Which Install the Host Has

A host installs the notification packages one of two ways (see [Two ways to install the host](../egress/operator-notifications.md#two-ways-to-install-the-host)). Tick the block that matches the deployment and skip the other. The items above are for the monolith install. In a split deployment, run the WebSocket and CORS items against the notification service and skip the migration item: this host no longer owns the Notification Center tables.

- [ ] **Monolith** (`Dignite.NotificationCenter.*`; `Dignite.Abp.Notifications`, the in-process implementation, comes with it): the host does not also reference `Dignite.Abp.Notifications.Client`. With both installed the host distributes by itself instead of sending its notifications to the notification service, and nothing fails to tell you.
- [ ] **Split deployment** (`Dignite.Abp.Notifications.Client` + `Dignite.Abp.Notifications.EntityFrameworkCore`, or `.MongoDB`): the host references no `Dignite.NotificationCenter.*` package, so `Dignite.Abp.Notifications` is not in its dependency tree either, and its `NotificationCenter` connection string points at the notification service's database.
- [ ] **Split deployment**: the notification service's `NotifDefinitions` table holds the four `VaultExtract.Document.*` notifications after this host's first start. Otherwise the service refuses the publish requests and its event inbox keeps retrying them.
- [ ] **Split deployment**: upload a small `.txt` document; the bell badge appears and `/signalr-hubs/notifications` is served by the notification service (the WebSocket request returns **101 Switching Protocols** from there).
- [ ] **Neither install** (no `Dignite.NotificationCenter.*`, no `.Client`): the host logs a warning for every notification and drops it (`NullNotificationPublisher`). That is correct only for a host that deliberately sends no operator notifications.
- [ ] **Upgrading from `abp-modules` 10.0.0-rc.22 or earlier**: `AbpBackgroundJobs` holds no row whose `JobName` is `Dignite.Abp.Notifications.NotificationDistributionJobArgs`; the job is now named `Dignite.Abp.Notifications.Distribute`, and rows under the old name are not picked up.

## See Also

- [Deployment](deployment.md)
- [Observability](observability.md)
- [Field Architecture v2 → v3 Migration](field-architecture-v3-migration.md)

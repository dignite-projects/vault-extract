```json
//[doc-seo]
{
    "Description": "Deploy the Dignite Vault Extract host: database, original-file storage, signing certificate, string encryption, OCR and AI providers, Docker, reverse proxy and migrations."
}
```

# Deployment

This page covers what a host operator configures to run the Vault Extract host: the database, the storage for original files, the signing certificate, the OCR and AI providers, the Docker layout and, if there is one, the reverse proxy. Per-feature settings are on each feature's page.

> Vault Extract outputs Markdown and structured metadata to downstream consumers (RAG platforms, business systems, MCP clients). It does **not** ship a vector database, an embedding pipeline or a chat platform; those belong to the downstream side.

## Topology

```text
Vault Extract host (ASP.NET Core)
  ├─► SQL Server ─── application data: documents, types and fields, identity, OpenIddict,
  │                  background jobs, event outbox and inbox, notifications
  ├─► BLOB storage ─ original uploaded files (container "extract-documents")
  ├─► LLM provider ─ classification, field extraction, segmentation, titles (IChatClient)
  └─► OCR provider ─ Vision-LLM (default) / PaddleOCR sidecar / Azure Document Intelligence

  egress: REST API · MCP server · distributed events · CSV / XLSX download
```

Vault Extract's state lives in two places: the relational database and the BLOB storage that holds each document's original file. Back them up together. Downstream consumers keep their own storage (vector database, business aggregates, search index).

## Connection Strings

The shipped host uses SQL Server through EF Core, with one connection string:

```json
"ConnectionStrings": {
  "Default": "Server=YOUR_DB_SERVER;Database=Extract;User ID=YOUR_USER;Password=__SET_FROM_SECRETS__;TrustServerCertificate=true"
}
```

Source the password from the platform's secret store (Azure Key Vault, AWS Secrets Manager, environment variables injected by the orchestrator), not from `appsettings.Production.json`. The schema itself is provider-agnostic; see [Database Portability](#database-portability).

## Original File Storage

Every upload's original file is written to the `extract-documents` BLOB container through [ABP BLOB Storing](https://abp.io/docs/latest/framework/infrastructure/blob-storing). Text extraction reads it, re-parse reads it again, and the document detail page downloads it, so losing it breaks those features for every stored document. Permanently deleting a document deletes its file.

The shipped host configures the **file system provider** as the default for every container, under `<content root>/App_Data/blobs`:

```csharp
Configure<AbpBlobStoringOptions>(options =>
{
    options.Containers.ConfigureDefault(container =>
    {
        container.UseFileSystem(fileSystem =>
        {
            fileSystem.BasePath = Path.Combine(
                hostingEnvironment.ContentRootPath, "App_Data", "blobs");
        });
    });
});
```

For production, either put that directory on durable, backed-up storage (a persistent volume in a container deployment), or replace the provider with one of ABP's other providers (Azure, AWS, MinIO, database, and so on) in `VaultExtractHostModule.ConfigureEfCore`. Several host instances must share the same storage.

> The Docker Compose layout below mounts no volume for `App_Data`. Original files written inside the API container are lost when the container is recreated. Mount one before using that layout for anything you keep.

## Authentication and Signing Certificate

The host is its own OpenIddict authorization server. Development mode generates temporary certificates; production needs a real signing certificate:

```bash
dotnet dev-certs https -v -ep openiddict.pfx -p <your-certificate-passphrase>
```

Place `openiddict.pfx` in the host's working directory and configure:

```json
"AuthServer": {
  "Authority": "https://your-host.example.com",
  "SwaggerClientId": "VaultExtract_Swagger",
  "CertificatePassPhrase": "<your-certificate-passphrase>"
}
```

`CertificatePassPhrase` should also come from the secret store. For token lifetimes, encryption credentials and rotation, see ABP's [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment).

## String Encryption Key

ABP encrypts some stored values (for example tenant connection strings) with `StringEncryption:DefaultPassPhrase`. **Never change it after data has been written**: encrypted values become unreadable.

```json
"StringEncryption": {
  "DefaultPassPhrase": "<a strong random passphrase, never rotated>"
}
```

`appsettings.secrets.json`, `appsettings.Production.json` and `appsettings.Staging.json` are git-ignored; create them at deploy time and never commit them. `appsettings.Development.json` is committed and holds no secrets. The host excludes `appsettings.secrets.json` from `dotnet publish`, so a developer's local overrides never reach a deployed server.

## Multi-Tenancy

The shipped host runs with multi-tenancy enabled (`VaultExtractHostModule.IsMultiTenant = true`). The Host and each tenant are separate layers: each defines its own document types and fields and sees only its own documents (see [Document Types and Fields](../documents/document-types.md)). `--migrate-database` migrates and seeds the Host database, then seeds every tenant (migrating a tenant's own database first when it has a separate connection string).

## OCR Provider

The host enables exactly **one** OCR provider ([comparison](../text-extraction/text-extraction.md#ocr--choosing-a-provider)):

- **Vision-LLM** (the shipped default): uses a vision-capable chat model through a keyed `IChatClient`; no sidecar. Strongest for phone photos, thermal receipts and image-only PDFs. See [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md).
- **PaddleOCR**: a local Docker sidecar on CPU; data never leaves the network. See [PaddleOCR](../text-extraction/ocr-paddleocr.md).
- **Azure Document Intelligence**: a cloud service for workloads whose data may leave the network. See [Azure Document Intelligence](../text-extraction/ocr-azure-document-intelligence.md).

Switching means changing the provider module in `VaultExtractHostModule`'s `[DependsOn]` together with the matching `<ProjectReference>` in `host/src/Dignite.Vault.Extract.Host.csproj`. Switching to or from Vision-LLM also adds or removes the keyed vision `IChatClient` registration in `ConfigureAI`.

## AI Provider

The keyed `IChatClient` registrations and model selection are described in [AI Provider](../configuration/ai-provider.md). Provider credentials are host configuration only: they never reach the Application or Domain layer, and end customers never configure them. The host registers no `IEmbeddingGenerator`; vectorization is the downstream RAG platform's job.

## Distributed Event Bus

The host configures ABP's transactional outbox and inbox on its `DbContext`, but references no message broker. Without one, distributed events are handled in process. To deliver [integration events](../egress/integration-events.md) to a separate consumer, add an ABP event bus provider (RabbitMQ, Kafka, Azure Service Bus, and so on) to the host.

## Docker

The Compose layout in `host/etc/docker/docker-compose.yml` defines:

- `vault-extract-web`: the Angular SPA
- `vault-extract-api`: the ASP.NET Core host
- `db-migrator`: the host image run once with `--migrate-database`
- `sql-server`: SQL Server (Azure SQL Edge image)

```bash
# Build images locally
cd host/etc/build
./build-images-locally.ps1

# Start the stack
cd host/etc/docker
./run-docker.ps1

# Stop containers
cd host/etc/docker
./stop-docker.ps1
```

For day-to-day development without image builds, see [Local Development](../get-started/local-development.md): it runs the host with `dotnet run` and starts only the sidecars from `host/docker-compose.yml`.

## Reverse Proxy

Skip this section if clients reach the host directly (Kestrel itself, or a load balancer that already forwards WebSockets).

The operator UI's notification bell opens a **WebSocket** to `/signalr-hubs/notifications` ([Operator Notifications](../egress/operator-notifications.md)). A WebSocket starts as an ordinary HTTP request with `Upgrade: websocket` and `Connection: Upgrade` headers, and many reverse proxies do not forward those two by default, so the handshake fails.

Nothing breaks when that happens: SignalR falls back to Server-Sent Events and then to long polling, and notifications still arrive, because the inbox (`/api/notification-center/notifications`) is the source of truth. The cost is higher latency and more open requests per signed-in user. Treat the proxy setting as part of a correct deployment, not as an outage fix.

This applies to the default install, where the host runs the Notification Center and serves the hub. In a split deployment the notification service serves the hub, so the proxy in front of that service needs the setting instead. See [Two ways to install the host](../egress/operator-notifications.md#two-ways-to-install-the-host).

nginx (the `map` goes in the `http` block):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    # ...
    location /signalr-hubs/ {
        proxy_pass         http://<host-address>;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade    $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host       $host;
        proxy_cache        off;
        proxy_buffering    off;
        proxy_read_timeout 100s;
    }
}
```

For any other proxy or gateway, make sure `/signalr-hubs/` passes the `Upgrade` and `Connection` headers (IIS needs the WebSocket Protocol feature) and check that product's WebSocket documentation.

The browser connects to the host directly from the SPA's origin, so `App:CorsOrigins` must list that origin; the REST API needs it already. After deploying, check it with the "Operator notifications" section of the [deployment checklist](deployment-checklist.md): the WebSocket request to `/signalr-hubs/notifications` must return `101 Switching Protocols`.

The MCP endpoint's rate limiter also needs to know about the proxy; see [Reverse Proxy Client IPs](../egress/mcp-server.md#reverse-proxy-client-ips).

## Migrations

EF Core migrations live under `host/src/Migrations/`. Apply them, and seed the initial data (the `admin` user, the `DocumentManager` and `Viewer` operator roles in the Host and in each tenant, and the OpenIddict clients), by running the host once with `--migrate-database`:

```bash
cd host/src
dotnet run --migrate-database
```

The process migrates, prints `Migration completed.` and exits; start the host normally afterwards. `host/migrate-database.ps1` runs the same command, and the Compose layout's `db-migrator` service does the same in a container. There is no separate DbMigrator project.

Releases that need more than a schema migration say so in `CHANGELOG.md`. The 0.3.x → 0.5.x field-storage change has its own runbook: [Field Architecture v2 → v3 Migration](field-architecture-v3-migration.md).

## Verifying a Release

When deploying to a new environment, upgrading a critical dependency, or shipping a change to the core pipeline, work through the [deployment checklist](deployment-checklist.md). Treat it as a per-release ticket template: copy the relevant sections and tick the boxes as you verify.

## Database Portability

SQL Server is the host baseline, but the schema is **provider-agnostic**: it uses ordinary relational columns (typed columns and `nvarchar(max)`), no native JSON column type and no provider-specific index DDL, so it applies on SQL Server, PostgreSQL and other relational providers.

Layer-scoped uniqueness — document types `(TenantId, TypeCode)`, fields `(TenantId, DocumentTypeId, Name)` and cabinets `(TenantId, Name)` — is enforced in the domain layer by `DocumentTypeManager`, `FieldDefinitionManager` and `CabinetManager`, **not** by a soft-delete-filtered unique index ([#304](https://github.com/dignite-projects/vault-extract/issues/304)). That removes two portability blockers: a provider-specific `HasFilter("IsDeleted = 0")` literal, and reliance on SQL Server treating `NULL`s as equal in a unique index for Host rows (`TenantId IS NULL`), which PostgreSQL does not do by default. Layer scoping comes from ABP's `IMultiTenant` filter, so the same key is allowed in the Host and in a tenant while a duplicate within one layer is rejected.

The accepted trade-off is a race between the check and the insert: two concurrent creates of the same key could both pass the check. That is acceptable for these low-frequency, admin-managed entities.

## See Also

- [Installation](../get-started/installation.md) — adding the modules to your own ABP host
- [Local Development](../get-started/local-development.md) — running on a developer machine
- [Deployment Checklist](deployment-checklist.md) — release smoke tests
- [Observability](observability.md) — OpenTelemetry export
- [Options and Features](../configuration/options.md) — every configuration section in one place

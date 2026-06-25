# Deployment

This page covers what a host operator needs to configure to run Extract: the relational database, the authentication signing certificate, the OCR sidecar, and the Docker layout. For per-feature configuration (OCR, AI provider) see the matching feature doc.

> **Channel positioning**: Dignite Vault Extract outputs Markdown + structured metadata to downstream consumers (RAG platforms, business systems, MCP clients). It does **not** ship a vector database, embedding pipeline, or chat platform — those belong on the downstream side. See `CLAUDE.md` → "OUT of scope".

## Topology

```text
Dignite Vault Extract Host (ASP.NET Core)
  ├─► SQL Server — relational application database (entities, audit, identity, OpenIddict, OutboxEvent)
  └─► OCR provider — Vision LLM (default, via IChatClient) / PaddleOCR sidecar / Azure Document Intelligence — text extraction
                                                                                                    
                  ↓ exports                                                                         
   REST API / MCP server / DistributedEventBus / Webhook (planned) — downstream consumers (RAG / business systems)
```

All Dignite Vault Extract state lives in the single SQL Server database. Markdown + event payloads flow out to downstream consumers; downstream consumers are responsible for their own storage (vector DB / business aggregates / search index).

## Connection strings

Dignite Vault Extract uses SQL Server as the only persistence backend.

```json
"ConnectionStrings": {
  "Default": "Server=YOUR_DB_SERVER;Database=Extract;User ID=YOUR_USER;Password=__SET_FROM_SECRETS__;TrustServerCertificate=true"
}
```

Production deployments should source the password from the platform's secret store (Azure Key Vault, AWS Secrets Manager, env vars injected by the orchestrator, etc.), not from `appsettings.Production.json`.

## Authentication and signing certificate

Dignite Vault Extract uses OpenIddict. Development mode auto-generates ephemeral certificates; production needs a real signing certificate.

Generate one with:

```bash
dotnet dev-certs https -v -ep openiddict.pfx -p <your-certificate-passphrase>
```

Place `openiddict.pfx` in the host working directory and configure:

```json
"AuthServer": {
  "Authority": "https://your-host.example.com",
  "SwaggerClientId": "Extract_Swagger",
  "CertificatePassPhrase": "<your-certificate-passphrase>"
}
```

`CertificatePassPhrase` should also come from the platform's secret store, not from a checked-in file.

For deeper OpenIddict configuration (token lifetimes, encryption-credential rotation, etc.) see the upstream guide: [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment).

## String encryption key

ABP stores some configuration values (e.g. tenant connection strings) encrypted at rest using `StringEncryption:DefaultPassPhrase`. **Never change this key once data has been written** — encrypted values become unreadable.

```json
"StringEncryption": {
  "DefaultPassPhrase": "<a strong random passphrase, never rotated>"
}
```

`appsettings.Development.json` is git-ignored; `appsettings.Production.json` should be created at deploy time and never committed.

## OCR provider

Dignite Vault Extract ships three OCR options ([comparison](../text-extraction/text-extraction.md#ocr--choosing-a-provider)):

- **Vision-LLM** (current default, #259) — `IChatClient`-based, no sidecar; reuses the host's keyed vision chat client. Strongest for phone photos / thermal receipts / image-only PDFs. See [ocr-vision-llm.md](../text-extraction/ocr-vision-llm.md).
- **PaddleOCR** — local Docker sidecar, CPU, never leaves the network. See [ocr-paddleocr.md](../text-extraction/ocr-paddleocr.md).
- **Azure Document Intelligence** — cloud option for production workloads that can leave the network. See [ocr-azure-document-intelligence.md](../text-extraction/ocr-azure-document-intelligence.md).

Host module wires exactly one via `[DependsOn(...)]` + matching `<ProjectReference>` in `host/src/Dignite.Vault.Extract.Host.csproj` (switching to/from Vision-LLM also means adding/removing its keyed vision `IChatClient` registration in `ConfigureAI`).

## AI provider

The keyed `IChatClient` registrations (title generator + structured, plus a vision client when the default Vision-LLM OCR provider is enabled) and their model id selection are covered in [ai-provider.md](../configuration/ai-provider.md). Provider wiring is host-only — credentials never reach the Application or Domain layer. The host does **not** register an `IEmbeddingGenerator` — vectorization is downstream RAG's responsibility, not the channel's.

> **CLAUDE.md constraint**: LLM provider + API key are configured at the host deployment layer, **not** exposed for end-user configuration. Letting business users fill API keys is a product-philosophy mistake (they are not technical users).

## Docker

The deployment Docker Compose layout in `host/etc/docker/docker-compose.yml` wires:

- `extract-web` — Angular SPA
- `extract-api` — ASP.NET Core API
- `db-migrator` — runs `dotnet run --migrate-database` once at startup
- `sql-server` — SQL Server (Azure SQL Edge image for local-equivalent dev)

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

For local development without the full image build, see [local-development.md](../get-started/local-development.md) — it runs the API via `dotnet run` against a local SQL Server (LocalDB or container) and only spins up the PaddleOCR / observability sidecars via `host/docker-compose.yml`.

## Migrations

EF Core migrations live under `host/src/Migrations/`. Apply them with:

```bash
cd host/src
dotnet run -- --migrate-database
```

Or use ABP's `Dignite.Vault.Extract.DbMigrator` console runner if your deployment topology calls for a separate migration step (it also seeds initial admin / OpenIddict client data).

## Verifying a release

When deploying to a new environment, upgrading critical dependencies, or shipping changes that touch the core pipeline, run through [deployment-checklist.md](deployment-checklist.md). Treat it as a per-release ticket template — copy the relevant sections and tick boxes as you verify.

## See also

- [Local development setup](../get-started/local-development.md) — running on a developer laptop
- [Text extraction](../text-extraction/text-extraction.md) — choosing and configuring an OCR provider
- [AI provider](../configuration/ai-provider.md) — wiring the keyed `IChatClient` registrations
- [Deployment checklist](deployment-checklist.md) — release smoke tests
- [Observability](observability.md) — OpenTelemetry export targets

## Database portability

SQL Server is the host baseline, but the schema is **provider-agnostic**: it emits no provider-specific index DDL, so it applies cleanly on SQL Server, PostgreSQL, and other relational providers with no per-provider re-evaluation.

Layer-scoped uniqueness — `DocumentTypes (TenantId, TypeCode)`, `FieldDefinitions (TenantId, DocumentTypeId, Name)`, `ExportTemplates (TenantId, Name)`, and `Cabinets (TenantId, Name)` — is enforced in the application/domain layer by dedicated domain services (`DocumentTypeManager` / `FieldDefinitionManager` / `ExportTemplateManager` / `CabinetManager`), **not** by a soft-delete-filtered DB unique index (#304). This is the ABP-idiomatic approach and removes the previous portability blockers: the non-portable `HasFilter("IsDeleted = 0")` literal, and the reliance on SQL Server's "a unique index treats NULLs as equal" semantics for Host-layer rows (`TenantId IS NULL`) — semantics PostgreSQL does not share by default (`NULLS DISTINCT`). Layer scoping is delegated to ABP's `IMultiTenant` global filter, so the same key is allowed across the Host and tenant layers while a duplicate within one layer is rejected.

The accepted tradeoff is a TOCTOU race window in the SELECT-then-INSERT check: two concurrent creates of the same key could both pass the check and both insert. This is acceptable for these four low-frequency, admin-managed configuration entities. If a high-concurrency write path is ever added for them, revisit (serializable unit of work / advisory lock / a portable normalized-column unique index).

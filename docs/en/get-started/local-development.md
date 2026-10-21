```json
//[doc-seo]
{
    "Description": "Run Dignite Vault Extract from source on a developer machine: prerequisites, LLM and database configuration, migrations, the Angular SPA, sidecars and proxy regeneration."
}
```

# Local Development

This guide runs the Vault Extract repository on a developer machine: the .NET host, the Angular operator UI, and the optional Docker sidecars. To add the modules to an application of your own instead, see [Installation](installation.md).

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 (`global.json` pins `10.0.100`, rolling forward to the latest feature band) | |
| [Node.js](https://nodejs.org) | `^22.22.3` or `^24.15.0` | Required by Angular 22 |
| SQL Server | 2019 or later | LocalDB, Express and Developer all work; the committed `appsettings.json` points at `(LocalDb)\MSSQLLocalDB` |
| An LLM provider | Any OpenAI-compatible endpoint | Mandatory; see [Backend Configuration](#backend-configuration). A local Ollama works |
| [ABP CLI](https://abp.io/docs/latest/cli) | Matching ABP 10.7 | `dotnet tool install -g Volo.Abp.Studio.Cli`; used for `abp install-libs` and proxy generation |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | Any recent | Optional: the PaddleOCR sidecar and the local OpenTelemetry dashboard |

## Backend Configuration

Create `host/src/appsettings.secrets.json`. The host loads it last (ABP's `AddAppSettingsSecretsJson()`), the file is git-ignored, and `dotnet publish` leaves it out. Keep real credentials there or in environment variables, never in the committed `appsettings.json` or `appsettings.Development.json`.

An LLM provider is **required**. The host fails at startup while `Vault:Extract` is unconfigured, because classification and field extraction have no non-LLM fallback (the committed `appsettings.json` ships a `"YOUR_API_KEY"` placeholder, which the startup check rejects). A minimal configuration:

```json
{
  "ConnectionStrings": {
    "Default": "Server=YOUR_DB_SERVER;Database=Extract-Dev;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=true"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  },
  "Vault": {
    "Extract": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "ChatModelId": "gpt-4o-mini",
      "VisionOcrModelId": "gpt-4o-mini"
    }
  }
}
```

- With LocalDB, the committed connection string already works and you can leave `ConnectionStrings` out.
- `VisionOcrModelId` is required because the host's default OCR provider is [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md); it must name a vision-capable model.
- `TitleGeneratorModelId` and `StructuredModelId` are optional and default to `ChatModelId`.
- For Azure OpenAI, OpenRouter, Ollama and other providers, see [AI Provider](../configuration/ai-provider.md). For a zero-cost local option, run Ollama and point `Endpoint` at its `/v1` endpoint with any non-empty `ApiKey`.

> **OpenIddict certificates.** In Development mode temporary signing and encryption certificates are generated automatically; no `.pfx` file is needed.

## Running the Backend

Install the client-side libraries once after cloning, and again when they change:

```bash
cd host/src
abp install-libs
```

Create the database schema and seed the initial data. The command migrates, prints `Migration completed.` and exits; run it again after pulling a change that adds a migration:

```bash
cd host/src
dotnet run --migrate-database
```

Then start the host:

```bash
cd host/src
dotnet run
```

The API listens on `https://localhost:44348`; Swagger UI is at `https://localhost:44348/swagger`.

> In Development the seed also creates a **Sample Document** type in the Host layer, with one field of each field type and one processed sample document, so the field designer and the egress paths have something to show. It lives in the host project only; the modules ship no document types.

## Running the Angular UI

The Angular workspace lives in the repository-root `angular/` directory (Angular CLI). The `host` application consumes the `vault-extract` library project from source:

```bash
cd angular
npm install
npm start
```

The SPA is served at `http://localhost:4200`. The operator UI is under **Vault Extract** in the main menu (`/vault-extract/...`).

Default credentials, seeded by `--migrate-database`:

| Field | Value |
| --- | --- |
| Username | `admin` |
| Password | `1q2w3E*` |

## Optional Sidecars (Docker)

`host/docker-compose.yml` defines two sidecars, neither needed by the default configuration:

| Service | Port | Purpose | Profile |
| --- | --- | --- | --- |
| **PaddleOCR** | 8866 | Local OCR sidecar (PP-StructureV3, CPU). Needed only when you switch the host to the [PaddleOCR](../text-extraction/ocr-paddleocr.md) provider | default |
| **aspire-dashboard** | 18888 (UI), 4317 (OTLP gRPC) | Local OpenTelemetry backend at `http://localhost:18888` | `observability` |

```bash
cd host
docker compose up -d paddleocr
docker compose --profile observability up -d aspire-dashboard
```

PaddleOCR answers `{"status":"success"}` on `http://localhost:8866/ping` once ready; its first start downloads about 600 MB of model weights. The committed `appsettings.Development.json` already turns the OpenTelemetry export on (`OpenTelemetry:Enabled`), pointed at this dashboard; see [Observability](../deployment/observability.md). Stop the sidecars with `docker compose down`.

> If your machine can run a SQL Server container, the deployment layout in `host/etc/docker/docker-compose.yml` starts SQL Server, the API and the SPA together. For day-to-day iteration, `dotnet run` against a local SQL Server is faster.

## Regenerating the HTTP Client Proxies

After adding or changing an API endpoint, regenerate the Angular proxies. The host must be running:

```bash
cd angular
npm run generate-proxy
```

This runs `abp generate-proxy -t ng` against `https://localhost:44348` and rewrites the files under `angular/projects/vault-extract/src/lib/proxy/`. Commit the regenerated files together with the API change.

## Running the Tests

```bash
dotnet test core/test/Dignite.Vault.Extract.Application.Tests/Dignite.Vault.Extract.Application.Tests.csproj
```

The core test projects are ABP integration tests over in-memory SQLite; each project under `core/test/` runs on its own, and the whole core suite takes about a minute. The Angular library's tests run from `angular/`:

```bash
npx ng test vault-extract --watch=false
```

## Troubleshooting

### The Host Stops at Startup with "Extract requires an LLM provider"

`Vault:Extract:Endpoint`, `ApiKey` or `ChatModelId` is missing, or `ApiKey` is still `YOUR_API_KEY`. Check that `appsettings.secrets.json` sits in `host/src` next to `appsettings.json`. A message about `VisionOcrModelId` means the vision model id is missing.

### Images Come Back with Empty Markdown

The default OCR provider needs `Vault:Extract:VisionOcrModelId` to name a **vision-capable** model. A text-only model returns nothing useful for an image. See [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md).

### Port Conflicts

If Docker cannot bind a port, another process already uses it:

```bash
# Windows
netstat -ano | findstr ":8866 :18888 :4317"

# Linux / WSL
ss -tlnp | grep -E '8866|18888|4317'
```

### Database Migration Errors

Migrations need `CREATE TABLE` and `CREATE INDEX` rights on the target database. LocalDB's default user has them; on a shared SQL Server, grant the application user `db_owner` on the Vault Extract database.

### PaddleOCR Is Slow on the First Request

PaddleOCR loads the PP-StructureV3 model on the first request, which takes 30 to 60 seconds after a cold start. Later requests are fast.

## See Also

- [Installation](installation.md)
- [AI Provider](../configuration/ai-provider.md)
- [Text Extraction](../text-extraction/text-extraction.md)
- [Deployment](../deployment/deployment.md)

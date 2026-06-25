# Local Development Setup

This guide covers everything needed to run Dignite Vault Extract on a local machine.

> **Channel positioning**: Dignite Vault Extract is a paper → digitized-data channel. It persists into **SQL Server**; downstream RAG / vector-store / chat features live in the consumer's own deployment, not here. See `CLAUDE.md` → "OUT of scope".

## Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 | |
| [Node.js](https://nodejs.org) | 18 | Required for Angular frontend |
| SQL Server | 2019+ | LocalDB / Express / Developer all work. `appsettings.json` defaults to `(LocalDb)\MSSQLLocalDB` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | any recent | Runs the PaddleOCR sidecar (and optional observability backend) |
| ABP CLI | latest | `dotnet tool install -g Volo.Abp.Cli` |

---

## Infrastructure services (Docker)

The repo's `host/docker-compose.yml` runs two sidecars — one required for OCR, one optional for OpenTelemetry visualization:

| Service | Port | Purpose | Profile |
|---------|------|---------|---------|
| **PaddleOCR** | 8866 | OCR sidecar for scanned documents (PP-StructureV3, CPU mode). Required if you upload image / scanned-PDF documents | default |
| **aspire-dashboard** | 18888 (UI), 4317 (OTLP gRPC) | Local OpenTelemetry backend — renders traces / metrics / logs at `http://localhost:18888`. Optional but recommended | `observability` |

Start the required sidecar:

```bash
cd host
docker compose up -d paddleocr
```

Add the dashboard when you want to see traces / metrics:

```bash
docker compose --profile observability up -d aspire-dashboard
```

See [observability.md](../deployment/observability.md) for what's emitted and how to point at a different OTLP backend (Jaeger / Datadog / Tempo / Azure Monitor).

Verify PaddleOCR is ready:

```bash
curl http://localhost:8866/ping
# Expected: {"status":"success"}
```

To stop the sidecars:

```bash
docker compose down
```

> If your machine can run a SQL Server container (Windows Pro / Linux), the deployment Compose layout at `host/etc/docker/docker-compose.yml` brings up SQL Server + the API + Angular together. For pure dev iteration the lighter "PaddleOCR sidecar + local SQL Server + `dotnet run`" loop is faster.

---

## Backend configuration

Create `host/src/appsettings.Development.json`. This file is git-ignored — keep your real credentials here (or in user-secrets / environment variables), never in the committed `appsettings.json`.

An LLM provider is **required**. The host fails fast at startup if `Extract` is not configured, because document classification and field extraction have no non-LLM fallback (the committed `appsettings.json` ships only a `"YOUR_API_KEY"` placeholder, which the startup guard rejects). Minimum viable dev config:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  },
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
      "ChatModelId": "gpt-4o-mini"
    }
  }
}
```

`TitleGeneratorModelId` / `StructuredModelId` are optional and default to `ChatModelId`; set them only when you want a different model per workload.

To use a non-OpenAI provider (Azure OpenAI, SiliconFlow, OpenRouter, Ollama, etc.), see [docs/ai-provider.md](../configuration/ai-provider.md) — for OpenAI-protocol providers you only swap `Endpoint` + `ApiKey` + model ids; for non-OpenAI wire protocols you override `ConfigureAI` in `ExtractHostModule`. For a zero-cost local option, run Ollama and point `Endpoint` at its `/v1` endpoint (use any non-empty token as `ApiKey`).

> **OpenIddict certificates**: In Development mode, temporary signing and encryption certificates are generated automatically. No `.pfx` file is needed.

---

## Running the backend

Install client-side libraries (run once after cloning, or when dependencies change):

```bash
cd host/src
abp install-libs
```

Apply database migrations and seed initial data, then start the API:

```bash
cd host/src
dotnet run
```

The API will be available at `https://localhost:44348`. Swagger UI: `https://localhost:44348/swagger`.

---

## Running the Angular frontend

The Angular SPA lives in the repository-root `angular/` directory (an Nx workspace):

```bash
cd angular
npm install
npm start
```

The SPA will be available at `http://localhost:4200`.

Default credentials (seeded on first run):

| Field | Value |
|-------|-------|
| Username | `admin` |
| Password | `1q2w3E*` |

---

## Regenerating HTTP client proxies

After adding or modifying backend API endpoints, regenerate the Angular HTTP client proxies so the frontend stays in sync. The backend API must be running first (`dotnet run` in `host/src`).

```bash
cd angular
npm run generate-proxy
```

This uses `@abp/nx.generators` (a local Nx generator — no global `nx` or `ng` install needed) to read the Swagger spec from `https://localhost:44348` and regenerate the proxy files under `angular/packages/vault-extract/src/lib/proxy/`.

> Do **not** use `abp generate-proxy -t ng` (requires `angular.json`, which Nx projects don't have) or bare `nx g` / `ng g` (require global installs). `npm run generate-proxy` is the only correct entry point.

Commit the regenerated proxy files together with the API change.

---

## Full startup checklist

1. SQL Server is running and the target database (e.g. `Dignite Vault Extract-Dev`) is reachable
2. `docker compose up -d paddleocr` completed successfully in `host/` (only required if you upload scanned documents)
3. `host/src/appsettings.Development.json` exists with a valid connection string, passphrase, and `Extract` provider config (the host won't start without it — see [Backend configuration](#backend-configuration))
4. `dotnet run` started without errors in `host/src`
5. `npm start` started in `angular/`

---

## Troubleshooting

### Port conflicts

If Docker fails to bind a port, another process is already using it. Check with:

```bash
# Windows
netstat -ano | findstr ":8866 :18888 :4317"

# Linux / WSL
ss -tlnp | grep -E '8866|18888|4317'
```

### Database migration errors

Migrations require the SQL Server account to have `CREATE TABLE` / `CREATE INDEX` privileges on the target database. For LocalDB the default user typically has full rights; for a shared SQL Server instance grant the application user `db_owner` on the Dignite Vault Extract database.

### PaddleOCR slow on first request

PaddleOCR loads the PP-StructureV3 model into memory on the first request. Subsequent requests are fast. The first upload after a cold start may take 30–60 seconds.

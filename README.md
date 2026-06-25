# Dignite Vault Extract

> **Dignite Vault Extract = any content requiring IDP (Intelligent Document Processing) — scans / photos / PDF images / Office files / digital-born documents → trustworthy structured data.**
> A **channel layer**, not an end-product. It doesn't consume, doesn't own, doesn't dive into business — it hands Markdown + structured metadata to downstream RAG platforms, business systems, and AI clients via REST / EventBus / MCP server / Webhook (planned).

For the full positioning, architecture rules, OUT-of-scope list, Markdown-first contract, multi-stage ETO event contract, and security covenant, see [CLAUDE.md](./CLAUDE.md). It is the truth source — this README only stages the operational entry points.

## Data flow

```
content requiring IDP: scans / photos / PDF images / Office files / digital-born documents
    ↓
[Dignite Vault Extract channel]: OCR + Markdown + system metadata + type-bound field extraction
    ↓ (REST / EventBus / MCP server / Webhook — planned)
    ├─→ downstream RAG platform
    ├─→ business systems (finance / CLM / HR / ERP)
    ├─→ AI clients (Claude Desktop / Cursor / any MCP client)
    └─→ any consumer (build your own subscriber)
```

## Solution structure

```
extract/
├── core/      # Channel implementation — ABP layers (Abstractions / Domain.Shared / Domain / Application / EntityFrameworkCore / HttpApi / Mcp)
├── host/      # Host application — provider wiring (OCR + AI) and middleware (ASP.NET Core API)
├── angular/   # Angular SPA (operator UI)
└── docs/      # Operator-facing documentation (design decisions go to GitHub Issues, not here)
```

Business modules (contract management / invoice management / HR records / etc.) are **not** in this repo — they belong on the downstream consumer side per the channel philosophy.

## Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 | |
| [Node.js](https://nodejs.org) | 20 | Required for the Angular frontend (Angular 21 needs Node 20.19+ / 22.12+) |
| SQL Server | 2019+ | LocalDB works for development; production runs full SQL Server |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | any recent | Optional but recommended — runs the PaddleOCR sidecar and the local OpenTelemetry dashboard |

## Getting started (local development)

### 1. Start the PaddleOCR sidecar (only if you enable the PaddleOCR provider)

The host currently wires the **Vision LLM** OCR provider by default (see [Choosing an OCR provider](#choosing-an-ocr-provider)), which needs no sidecar — it reuses the `Extract` AI-provider configuration below. If you switch the host to the PaddleOCR provider, start its Docker container first:

```bash
cd host
docker compose up -d paddleocr
```

First run downloads ~600 MB of model weights and takes 30–60 seconds. Subsequent starts are instant.

### 2. Configure the database and the AI provider

Create `host/src/appsettings.Development.json` with your local SQL Server connection string and an LLM provider key:

```json
{
  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
  "ConnectionStrings": {
    "Default": "Server=YOUR_DB_SERVER;Database=Extract-Dev;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=true"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  },
  "Vault": {
    "Extract": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "YOUR_REAL_API_KEY",
      "ChatModelId": "gpt-4o-mini",
      "VisionOcrModelId": "gpt-4o-mini"
    }
  }
}
```

> This file is git-ignored. In Development mode, the application automatically generates temporary OpenIddict certificates — no `.pfx` file is needed. For LocalDB, the committed `appsettings.json` default (`Server=(LocalDb)\MSSQLLocalDB;...`) already works without any override.

An LLM provider is **mandatory** — classification and field extraction have no non-LLM fallback, and the host fails fast at startup while `Vault:Extract:ApiKey` is still the committed placeholder. Any OpenAI-compatible endpoint works; with the default Vision LLM OCR provider, `VisionOcrModelId` must point at a vision-capable model. See [AI provider](./docs/en/configuration/ai-provider.md).

### 3. Install client-side libraries

```bash
cd host/src
abp install-libs
```

### 4. Initialize the database (first run only)

```bash
cd host/src
dotnet run --migrate-database
```

This creates the database schema and seeds the admin account (`admin` / `1q2w3E*`). Only needed once per fresh database.

### 5. Run the backend

```bash
cd host/src
dotnet run
```

API: `https://localhost:44348`. Swagger: `https://localhost:44348/swagger`.

### 6. Install frontend dependencies and run Angular

The Angular SPA lives in the repository-root `angular/` directory (an Nx workspace):

```bash
cd angular
npm install
npm start
```

SPA: `http://localhost:4200`. Default seeded credentials: `admin` / `1q2w3E*`.

## Choosing an OCR provider

Dignite Vault Extract ships three OCR providers; the host enables exactly one (`[DependsOn(...)]` in `host/src/ExtractHostModule.cs` + the matching `ProjectReference` in `host/src/Dignite.Vault.Extract.Host.csproj`):

* **Vision LLM** — the host's current default (#259). Sends images / rasterized PDF pages to a vision-capable `IChatClient` model; the strongest option for phone photos, thermal receipts, and image-only PDFs. No sidecar — only a vision model id. See [docs/en/text-extraction/ocr-vision-llm.md](./docs/en/text-extraction/ocr-vision-llm.md).
* **PaddleOCR** — local Docker sidecar (PP-StructureV3, CPU); data never leaves the network. See [docs/en/text-extraction/ocr-paddleocr.md](./docs/en/text-extraction/ocr-paddleocr.md).
* **Azure Document Intelligence** — cloud option (`prebuilt-layout`, high accuracy) when data is allowed to leave the network. See [docs/en/text-extraction/ocr-azure-document-intelligence.md](./docs/en/text-extraction/ocr-azure-document-intelligence.md). **Not yet validated against a live Azure resource — community testing welcome ([#327](https://github.com/dignite-projects/vault-extract/issues/327)).**

Full selection guidance, configuration, and resource footprint: see [docs/en/text-extraction/text-extraction.md](./docs/en/text-extraction/text-extraction.md).

## Deploying to production

For database connection strings, OpenIddict signing certificate, string-encryption key, and the Docker layout, see [docs/en/deployment/deployment.md](./docs/en/deployment/deployment.md). For per-release smoke tests, see [docs/en/deployment/deployment-checklist.md](./docs/en/deployment/deployment-checklist.md).

## Documentation

Start at the **[documentation index](./docs/en/index.md)**. Feature docs are grouped to follow the channel's data flow:

**Get started**

* [Local development setup](./docs/en/get-started/local-development.md) — prerequisites, Docker sidecars, configuration, troubleshooting

**Text extraction** (OCR + Markdown)

* [Text extraction](./docs/en/text-extraction/text-extraction.md) — Markdown-first contract, the two extraction paths, OCR provider comparison
* [PaddleOCR](./docs/en/text-extraction/ocr-paddleocr.md) — local OCR sidecar (PP-StructureV3, CPU); model choice and resource footprint
* [Azure Document Intelligence](./docs/en/text-extraction/ocr-azure-document-intelligence.md) — cloud OCR (`prebuilt-layout`); resource setup and F0 tier limits
* [Vision-LLM OCR](./docs/en/text-extraction/ocr-vision-llm.md) — multimodal-`IChatClient` OCR for photos / thermal receipts / image-only PDFs

**Pipeline**

* [Classification](./docs/en/pipeline/classification.md) — document-type pipeline and prompt tuning
* [Reprocessing](./docs/en/pipeline/reprocessing.md) — bulk re-run of classification / field extraction over existing documents after a config change
* [Pipeline runs](./docs/en/pipeline/pipeline-runs.md) — run history and review-UI payloads

**Egress**

* [Export templates](./docs/en/egress/export-templates.md) — per-tenant CSV / XLSX file egress: field projection, rename, ordering — zero business transformation
* [MCP server](./docs/en/egress/mcp-server.md) — document resources + structured search tool over Streamable HTTP, OpenIddict Bearer auth

**Configuration**

* [AI provider](./docs/en/configuration/ai-provider.md) — provider wiring for the two keyed chat clients (title generator + structured)

**Deployment & operations**

* [Deployment](./docs/en/deployment/deployment.md) — DB, certificate, Docker
* [Deployment checklist](./docs/en/deployment/deployment-checklist.md) — per-release smoke tests
* [Observability](./docs/en/deployment/observability.md) — OpenTelemetry pipeline, aspire-dashboard for local dev, switching OTLP backends

External references:

* [ABP Framework Documentation](https://abp.io/docs/latest)
* [Application (Single Layer) Startup Template](https://abp.io/docs/latest/solution-templates/application-single-layer)
* [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment)

## License

Dignite Vault Extract is licensed under the [Apache License 2.0](./LICENSE).

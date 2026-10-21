```json
//[doc-seo]
{
    "Description": "Documentation of Dignite Vault Extract, the ABP module that turns scans, photos, PDFs and Office files into Markdown, classified types and extracted fields for downstream systems."
}
```

# Dignite Vault Extract

**Dignite Vault Extract** is the channel layer that turns content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files and digital documents — into trustworthy structured data: **Markdown**, a classified **document type**, and **field values** extracted by a schema you define. It hands that data to downstream consumers (RAG platforms, business systems, AI clients) over a REST API, an MCP server and distributed events.

It is an [ABP Framework](https://abp.io) application module (ABP 10.7, .NET 10, Angular 22), licensed under LGPL-3.0-only.

```text
scans · photos · PDFs · Office files
        │
        ▼
  text extraction ──► classification ──► field extraction ──► Ready
  (Markdown, OCR)     (document type)    (your fields)
        │
        ▼
  REST API · MCP server · distributed events · CSV / XLSX download
        │
        ├─► RAG platforms
        ├─► finance / CLM / HR / ERP systems
        └─► Claude Desktop, Cursor and other MCP clients
```

## What It Does and Does Not Do

- **Markdown first.** Every document becomes Markdown, the one text payload of every egress. Headings and tables survive, so downstream chunkers and LLMs can use them.
- **You define the schema.** Vault Extract ships no document types and no business fields. Administrators define types and fields per layer (the Host, and each tenant separately); the channel classifies and extracts by them.
- **A trusted signal.** A document is published as Ready only when nothing needs review: a confirmed type, no suspected duplicate, no unresolved field problem. Everything else waits in an operator review queue.
- **Providers are host configuration.** The LLM and OCR providers and their keys are configured by whoever deploys the host, never by end users.
- **A channel, not an application.** Vector search, RAG Q&A, chat, approval workflows and business records (contracts, invoices) are out of scope: they belong to the downstream systems that consume the egress.

## Where to Start

| You want to… | Read |
| --- | --- |
| Run this repository on your machine | [Local Development](get-started/local-development.md) |
| Add the modules to your own ABP application | [Installation](get-started/installation.md) |
| Define what gets extracted | [Document Types and Fields](documents/document-types.md) |
| Operate the review queue | [Documents and Review](documents/documents.md) |
| Integrate a downstream system | [Integration Events](egress/integration-events.md), [REST API](egress/rest-api.md) |
| Connect an AI client | [MCP Server](egress/mcp-server.md) |
| Deploy to production | [Deployment](deployment/deployment.md), [Deployment Checklist](deployment/deployment-checklist.md) |

## Documentation Map

### Get Started

- [Installation](get-started/installation.md) — packages, module dependencies, database, host configuration and the Angular UI
- [Local Development](get-started/local-development.md) — running the repository's host and SPA, sidecars, tests, troubleshooting

### Documents

- [Documents and Review](documents/documents.md) — uploading, lifecycle statuses, review reasons, operator actions, recycle bin
- [Document Types and Fields](documents/document-types.md) — the Host and tenant layers, field types, duplicate detection, validation warnings, type packs
- [Sub-Documents](documents/sub-documents.md) — splitting bundles and embedded documents
- [Cabinets](documents/cabinets.md) — the manual filing dimension

### Text Extraction

- [Text Extraction](text-extraction/text-extraction.md) — the Markdown-first contract, digital-document providers, OCR provider choice
- [Vision-LLM OCR](text-extraction/ocr-vision-llm.md) — the default: a vision-capable chat model for photos, receipts and image-only PDFs
- [PaddleOCR](text-extraction/ocr-paddleocr.md) — a local OCR sidecar
- [Azure Document Intelligence](text-extraction/ocr-azure-document-intelligence.md) — cloud OCR (`prebuilt-layout`)

### Pipeline

- [Classification](pipeline/classification.md) — how documents are assigned a type, and declared types
- [Reprocessing](pipeline/reprocessing.md) — batch re-classification and re-extraction, single-document re-parse
- [Pipeline Runs](pipeline/pipeline-runs.md) — run history, retries and the review UI's payloads

### Egress

- [REST API](egress/rest-api.md) — reading documents and field values, filters, the endpoint reference
- [MCP Server](egress/mcp-server.md) — resources and search tools for AI clients, OAuth discovery, tenant scope
- [Integration Events](egress/integration-events.md) — the distributed event contract, the Ready gate, delivery semantics
- [Data Download](egress/data-download.md) — CSV / XLSX download of the document list
- [Operator Notifications](egress/operator-notifications.md) — in-app notifications for the uploader (not part of the egress contract)

### Configuration

- [AI Provider](configuration/ai-provider.md) — the keyed chat clients, model choice, behavior limits
- [Per-Document-Type Permissions](configuration/document-type-permissions.md) — per-type grants, ownership and the module-wide permissions
- [Options and Features](configuration/options.md) — every configuration section, the feature and the permission tree

### Deployment and Operations

- [Deployment](deployment/deployment.md) — database, file storage, certificates, Docker, reverse proxy, migrations
- [Deployment Checklist](deployment/deployment-checklist.md) — per-release smoke tests
- [Observability](deployment/observability.md) — OpenTelemetry export
- [GDPR User-Data Erasure](deployment/gdpr-user-data-erasure.md) — erasing the uploader's name on request
- [Field Architecture v2 → v3 Migration](deployment/field-architecture-v3-migration.md) — upgrading a 0.3.x deployment that holds field values

## Design Decisions and Changes

Design decisions are recorded in [GitHub Issues](https://github.com/dignite-projects/vault-extract/issues), and every release's changes, including breaking ones and their migrations, in [CHANGELOG.md](https://github.com/dignite-projects/vault-extract/blob/main/CHANGELOG.md). These pages describe the current behavior.

## See Also

- [ABP Framework Documentation](https://abp.io/docs/latest)
- [Dignite abp-modules](https://github.com/dignite-projects/abp-modules) — FlexFields, the shared MCP server and Notifications, which Vault Extract builds on

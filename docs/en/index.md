# Dignite Vault Extract Documentation

> **Dignite Vault Extract** is the **channel layer** that turns any content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files, and other document content — into trustworthy structured data, and exposes it to downstream consumers (RAG platforms, business systems, AI clients) over REST / EventBus / MCP.

These docs are operator- and deployer-facing. They are grouped to follow the channel's data flow — **get started → text extraction → pipeline → egress** — plus **configuration** and **deployment / operations**. Design decisions live in [GitHub Issues](https://github.com/dignite-projects/vault-extract/issues), not here.

## Get started

- [Local development setup](get-started/local-development.md) — prerequisites, Docker sidecars, configuration, troubleshooting

## Text extraction (OCR + Markdown)

The Markdown-first extraction layer: choosing and configuring an OCR provider.

- [Text extraction](text-extraction/text-extraction.md) — Markdown-first contract, the two extraction paths, OCR provider comparison
- [PaddleOCR](text-extraction/ocr-paddleocr.md) — local OCR sidecar (PP-StructureV3, CPU); model choice and resource footprint
- [Azure Document Intelligence](text-extraction/ocr-azure-document-intelligence.md) — cloud OCR (`prebuilt-layout`); resource setup and F0 tier limits
- [Vision-LLM OCR](text-extraction/ocr-vision-llm.md) — multimodal-`IChatClient` OCR for photos / thermal receipts / image-only PDFs

## Pipeline

How a document moves from extracted Markdown to a confirmed type and extracted fields.

- [Classification](pipeline/classification.md) — document-type pipeline and prompt tuning
- [Reprocessing](pipeline/reprocessing.md) — bulk re-run of classification / field extraction over existing documents after a config change
- [Pipeline runs](pipeline/pipeline-runs.md) — run history and review-UI payloads

## Egress

The live egress channels that expose outputs to downstream consumers.

- [MCP server](egress/mcp-server.md) — document resources + structured search tool over Streamable HTTP, OpenIddict Bearer auth
- [Data Download](egress/data-download.md) — CSV / XLSX file egress for the human operator: the document list's filters, every field of the type, zero business transformation

## Configuration

- [AI provider](configuration/ai-provider.md) — provider wiring for the keyed chat clients (title generator + structured, plus the optional vision client)

## Deployment & operations

- [Deployment](deployment/deployment.md) — DB, certificate, Docker
- [Deployment checklist](deployment/deployment-checklist.md) — per-release smoke tests
- [Observability](deployment/observability.md) — OpenTelemetry pipeline, aspire-dashboard for local dev, switching OTLP backends

## External references

- [ABP Framework Documentation](https://abp.io/docs/latest)
- [Application (Single Layer) Startup Template](https://abp.io/docs/latest/solution-templates/application-single-layer)
- [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment)

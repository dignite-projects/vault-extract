# Dignite Document AI

> **Positioning (one sentence)**: Document AI = the **channel layer** that turns any content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files, and other document content — into trustworthy structured data.
> **It does not consume, own, or reach into business logic** — it exposes outputs to downstream consumers: RAG platforms, business systems, AI clients, etc.

An ABP framework project. Most rules under `.claude/rules/` carry a `paths:` glob and are **auto-loaded when you edit matching code** (those without `paths:` are always loaded). This file keeps only the guardrails and navigation you must know *before* touching any code; detailed mechanics are injected on demand via `paths`.

## Data flow

```
Content requiring IDP: scans / photos / PDF images / Office files / digital-born documents
    ↓
[Document AI channel]: OCR + Markdown + common metadata + optional custom field extraction
    ↓ (REST / EventBus / MCP server / Webhook)
    ├─→ Downstream RAG platform (RAG Q&A)
    ├─→ Finance / CLM / HR / ERP and other business systems
    ├─→ Claude Desktop / Cursor / any MCP client
    └─→ Any consumer (build your own as needed)
```

## Project layout

- **core/** - Document AI channel implementation (ABP application stack)
- **host/** - Host application: configures OCR / Markdown / LLM providers; the only place middleware may be configured
- **angular/** - Angular SPA (operator UI)
- **docs/** - Operations / configuration / API documentation; design decisions live in GitHub Issues, not under docs/

Business modules (contract / invoice / HR management, etc.) are **not** in this repo — they live on the downstream consumer side and integrate via EventBus / MCP / REST.

## Architecture (two layers + Host)

Document AI is a **two-layer architecture** (the business layer is out of scope):

- **Layer 1 — Core (`core/`)**: all channel capabilities. `Abstractions` (the bottom of the dependency topology — extension contracts: multi-stage ETOs + `ITextExtractor` / `TextExtractionContext` / `TextExtractionResult`, depending on no other Document AI project) + the standard ABP module stack (`Domain.Shared` / `Domain` / `Application` / `EntityFrameworkCore` / `HttpApi` / `Mcp`) + the pluggable text-extraction stack (`TextExtraction` orchestrator + `Ocr` contract + multiple providers). **All channel core capabilities live in the Application layer**; REST (`HttpApi`) and MCP (`Mcp`) are parallel egress adapters — protocol/transport concerns do not leak into Application.
- **Layer 2 — Downstream consumers (out of scope, not in this repo)**: business modules (contract / invoice / HR management, etc.) live in their own repos and consume via the egress contracts; `Document` is the truth source and business records are its derived projections.
- **Layer 3 — Host (`host/`)**: a container only — declares dependencies via `[DependsOn]`, configures providers in `ConfigureServices` + registers three keyed `IChatClient`s (`TitleGenerator` / `Structured` / `Vision`, for title generation / structured-output paths / VisionLlm OCR respectively; **does not register `IEmbeddingGenerator`** — the channel layer does no vectorization), **configures middleware only here** (`OnApplicationInitialization`), and implements no business logic.

> **Key constraint**: **LLM / OCR providers and API keys are configured at the host deployment layer, never exposed to end customers** — customers are business users, not technical users; making customers fill in API keys is a product-philosophy mistake.

**Core dependency constraints**:

- **One-way dependency**: `Abstractions` sits at the bottom, referenced by all upper layers
- **Orchestration in Application**: BackgroundJob / Workflow / PipelineRun lifecycle / Document read-write all live in `Application`
- **Business modules are out of Document AI**: downstream consumes decoupled via EventBus / MCP / REST; **no** new business-module dependency may be added inside Core

> Core project topology, OCR / Markdown provider implementations, and switching discipline: see `.claude/rules/text-extraction.md` (auto-loaded when editing text-extraction / OCR code).

## Document type system (two independent single layers)

**A document type is the container for type-bound fields.** It splits into the **Host deployment layer** (defined by Host admin, used only by Host's own documents) and the **tenant layer** (defined per-tenant by tenant admin, used only by that tenant's documents).

**Core constraints (strictly single-layer, no cross-layer mixing)**:

- **Host and tenant are two separate universes** — Host types are **not** automatically visible to tenants; tenant types are invisible to other tenants
- **Each Document matches only its own `TenantId`'s type layer** — classification candidate sets / field extraction both match a single layer by exact `Document.TenantId`; **there is no cross-layer union**
- **The same TypeCode across layers is two legitimate rows** (distinguished by TenantId) — downstream must consume by the `(TenantId, DocumentTypeCode)` tuple; TypeCode is not a global unique identity
- **No built-in document types** — all types are self-managed by admins via `IDocumentTypeAppService` CRUD; there is no module startup-registration path and no seed contributor

> Classification execution mechanism (LLM classification prompt / per-tenant layer switching / manual-correction re-trigger): see `.claude/rules/field-architecture.md`.

## Field architecture

Fields are organized into two kinds: **system common fields** (auto-produced by the pipeline, top-level typed columns on `Document`, type-independent: `Title` / `Markdown` / `DocumentTypeId` / review fields / `LifecycleStatus` / `Language`) + **type-bound fields** (mechanism B: extracted by schema, attached under some type, split into Host fields / tenant fields across two layers).

**Essence of mechanism (B)**: Document AI provides a generic "extract-by-schema" engine; Host / tenant configure the schema, and Document AI Core **presets no business field definitions** (contract amount / invoice number / tax amount, etc. are not hardcoded). Two independent single layers — `Document.TenantId` decides which layer's field definitions this document runs against; **never mix across layers**.

**Document field-extension hard constraints** (must hold before changing `Document.cs` / egress DTOs / `TextExtractionResult`):

1. **There is forever only one text-typed field: `Markdown`** (`Document.SetMarkdown` immutability is already enforced at the code level). Any derived text (Summary / Outline / SectionsJson) is projected on the consumer side via `MarkdownStripper.Strip`, **not persisted**. `Title` is an immutable display snapshot derived from Markdown, not a new text payload.
2. **Non-text fields are judged by "generic truth source vs. business-specific"**: generic ones (e.g. `PageBlocks`) may be added to `Document` (still requires an Issue to discuss shape); business-specific ones (contract amount / invoice number / ID-card name) are stored by downstream consumers in their own aggregate roots — **`Document` is not polluted**.
3. **Forbidden**: adding a `Dictionary<string,object>` generic extension bag to `Document` / `TextExtractionResult` / event payloads; **forbidden** to introduce a parallel plain-text field.

> Full system-field table, text-extraction provenance (#210), and mechanism-B implementation form (`FieldDefinition` / `DocumentExtractedField` / unique index / composite key): see `.claude/rules/field-architecture.md` (auto-loaded when editing Document / field code).

## Egress contracts

Four egress channels: **REST API** (HTTP, generic programmatic access) / **MCP server** (Claude Desktop / Cursor / any MCP client) / **EventBus** (ABP DistributedEventBus, business systems / custom consumers) / **Webhook** (legacy systems).

**Multi-stage events** (thin payloads — ID + key metadata, downstream pulls details back): `DocumentUploadedEto` → `OCRCompletedEto` → `DocumentClassifiedEto` → `FieldsExtractedEto` → `DocumentReadyEto`; plus lifecycle events `DocumentDeletedEto` / `DocumentRestoredEto` / `DocumentPermanentlyDeletedEto` (orthogonal to the pipeline).

**Ready gate**: **only `DocumentReadyEto` is gated** — a document must obtain a confirmed type (classification confidence ≥ that type's `ConfidenceThreshold`, or manual confirmation) before it fires; documents that fail are still stored, early-stage events still fire, and they enter the operator review queue. Primary downstream consumers subscribe to `DocumentReadyEto` by default.

**Delivery semantics**: ABP transactional outbox → **at-least-once, events are never lost**; dedup / replacement is the downstream's responsibility via `EventTime` idempotency; the channel layer maintains no event state table and does no in-flight replacement.

> Full event-contract table, Ready-gate details, EventTime idempotency rules, and the #196 OCR-confidence removal: see `.claude/rules/integration-events.md` (auto-loaded when editing ETO / EventHandler).

## OUT of scope (explicitly not done)

Before touching these boundaries, stop and open an Issue to discuss:

- **RAG application layer**: ❌ vectorization / vector storage / retrieval engine / Chat·RAG Q&A·NL search / Agent·Workflow orchestration / MCP **client** (server only, no calling external MCP tools) / standardized chunking
- **Business layer**: ❌ preset schemas for business field extraction (contract amount / invoice number, etc. — customers configure via mechanism B) / preset industry-vertical import templates / business workflows (approval / renewal) / business-system-specific connectors / business modules (contract·invoice·HR management, etc. — built by downstream)
- **Configuration layer**: ❌ letting end customers configure the LLM provider / API key (configured at the host deployment layer)

## Markdown-first (mandatory)

Markdown is the **sole text payload** of the egress:

- **Providers must output Markdown** — `ITextExtractor` / `IMarkdownTextProvider` / `IOcrProvider` implementations must not fall back to a plain-text path; plain-text-to-Markdown fallback is done inside the provider, and `OcrResult` / `TextExtractionResult` **expose no RawText field**
- **Persistence**: `Document.Markdown` is the only text field on the aggregate root; **forbidden** to introduce a parallel plain-text field on `Document` / event payloads
- **Plain-text projection**: computed on demand on the consumer side via `MarkdownStripper.Strip(...)`, **not persisted**, not exposed side-by-side in contracts
- **Out-of-band signals are orthogonal to Markdown**: coordinates / confidence / page metadata / form key-value, etc., if needed in future, as **named, strongly-typed, nullable** independent extension fields (e.g. `PageBlocks`); **open a separate Issue per signal**; **forbidden** to use a `Dictionary<string,object>` generic extension bag

> Markdown trade-offs for structured vs. unstructured documents, provider translation responsibility, and out-of-band signal extension decisions: see `.claude/rules/text-extraction.md`.

## Security conventions (all internal LLM call paths)

Apply to built-in LLM classification, unified field extraction (mechanism B), title generation, cabinet/slug/field-definition draft suggestions, VisionLlm OCR, etc. **Detailed anti-patterns and correct implementations: see `.claude/rules/llm-call-anti-patterns.md`** (auto-loaded when editing LLM call sites such as Workflow / Extraction / Mcp / Pipeline):

- **Fail-closed security assertion**: any query path triggered by an LLM or whose parameters are influenced by LLM output must explicitly call `IAuthorizationService.CheckAsync(...)` (`[Authorize]` does not fire on MCP / reflection / tool-dispatch paths) + a hard result-set cap `Take(N)`, with no raw SQL
- **PromptBoundary**: user-derived free text (title / partyName / summary / document content, etc.) must be wrapped by `PromptBoundary.WrapField(...)` before entering an LLM prompt or LLM-facing output
- **Description / Instructions are compile-time constants**: any LLM-facing description / instructions must be compile-time constants or pure static literals; **forbidden** to concatenate user-controlled strings at runtime
- **Multi-tenancy isolation**: rely on ABP's `IMultiTenant` global filter (do not hand-write `CurrentTenant.Id` predicates); **the only discipline — never `Disable<IMultiTenant>()` / `IgnoreQueryFilters()` to pierce it on an LLM-triggered path**

## Working rules

1. Development in core strictly follows the rules under `.claude/rules/` (most auto-load by `paths:`; when modifying ABP BackgroundJob / JobArgs you must read `.claude/rules/background-jobs.md`)
2. Do not configure middleware in core ABP modules — only in the host
3. **Decide whether an Issue is needed before changing**: changes touching channel boundaries (OCR pipeline / egress contracts / field architecture / document-type system / Markdown-first / security conventions), module boundaries, or Slice tasks — **stop first and tell the user to open a GitHub Issue before proceeding**; pure implementation-detail fixes (bug fixes, wording corrections) are recorded directly in the commit message
4. **Downstream-consumer questions**: business modules (contract / invoice management, etc.) are out of Document AI scope. For discussions involving downstream-consumer implementation, explicitly state it is out-of-scope; Document AI only guarantees stable egress contracts

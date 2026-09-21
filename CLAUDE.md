# Dignite Vault Extract

> **Positioning (one sentence)**: Dignite Vault Extract = the **channel layer** that turns any content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files, and other document content — into trustworthy structured data.
> **It does not consume, own, or reach into business logic** — it exposes outputs to downstream consumers: RAG platforms, business systems, AI clients, etc.

An ABP framework project. Most rules under `.claude/rules/` carry a `paths:` glob and are **auto-loaded when you edit matching code** (those without `paths:` are always loaded). This file keeps only the guardrails and navigation you must know *before* touching any code; detailed mechanics are injected on demand via `paths`.

## Data flow

```
Content requiring IDP: scans / photos / PDF images / Office files / digital-born documents
    ↓
[Dignite Vault Extract channel]: OCR + Markdown + common metadata + optional custom field extraction
    ↓ (REST / EventBus / MCP server / Webhook — planned)
    ├─→ Downstream RAG platform (RAG Q&A)
    ├─→ Finance / CLM / HR / ERP and other business systems
    ├─→ Claude Desktop / Cursor / any MCP client
    └─→ Any consumer (build your own as needed)
```

## Project layout

- **core/** - Dignite Vault Extract channel implementation (ABP application stack)
- **host/** - Host application: configures OCR / Markdown / LLM providers; the only place middleware may be configured
- **angular/** - Angular SPA (operator UI)
- **docs/** - Operations / configuration / API documentation; design decisions live in GitHub Issues, not under docs/

Business modules (contract / invoice / HR management, etc.) are **not** in this repo — they live on the downstream consumer side and integrate via EventBus / MCP / REST.

> **Naming convention**: the C# type / module prefix is **`VaultExtract`** — the namespace `Dignite.Vault.Extract` minus the company token, mirroring ABP's `Volo.Abp.Identity` → `AbpIdentity*Module` (e.g. `VaultExtractHttpApiModule`, `VaultExtractDbContext`, `VaultExtractErrorCodes`). **`Extract` alone is the extraction *verb*, never a type prefix** — `ExtractedField` / `*Extractor` / `TextExtraction*` / `FieldExtraction*` / `ExtractAsync` stay as-is. **Serialized string contracts are frozen even where they still read `Extract`**: error codes `"Extract:*"`, the `/Localization/Extract/` resource, config sections `Vault:Extract*`, the `"extract-documents"` blob container — rename the holder class, never the persisted string value (that is a wire / DB / i18n break → open an Issue first).

## Architecture (two layers + Host)

Dignite Vault Extract is a **two-layer architecture** (the business layer is out of scope):

- **Layer 1 — Core (`core/`)**: all channel capabilities. `Abstractions` (the bottom of the dependency topology — extension contracts: multi-stage ETOs + `ITextExtractor` / `TextExtractionContext` / `TextExtractionResult`, depending on no other Dignite Vault Extract project) + the standard ABP module stack (`Domain.Shared` / `Domain` / `Application` / `EntityFrameworkCore` / `HttpApi` / `Mcp`) + the pluggable text-extraction stack (`Parse` orchestrator + `Ocr` contract + multiple providers). **All channel core capabilities live in the Application layer**; REST (`HttpApi`) and MCP (`Mcp`) are parallel egress adapters — protocol/transport concerns do not leak into Application.
- **Layer 2 — Downstream consumers (out of scope, not in this repo)**: business modules (contract / invoice / HR management, etc.) live in their own repos and consume via the egress contracts; `Document` is the truth source and business records are its derived projections.
- **Layer 3 — Host (`host/`)**: a container only — declares dependencies via `[DependsOn]`, configures providers in `ConfigureServices` + registers three keyed `IChatClient`s (`TitleGenerator` / `Structured` / `Vision`, for title generation / structured-output paths / VisionLlm OCR respectively; **does not register `IEmbeddingGenerator`** — the channel layer does no vectorization), **configures middleware only here** (`OnApplicationInitialization`), and implements no business logic.

> **Key constraint**: **LLM / OCR providers and API keys are configured at the host deployment layer, never exposed to end customers** — customers are business users, not technical users; making customers fill in API keys is a product-philosophy mistake.

**Core dependency constraints**:

- **One-way dependency**: `Abstractions` sits at the bottom, referenced by all upper layers
- **Orchestration in Application**: BackgroundJob / Workflow / PipelineRun lifecycle / Document read-write all live in `Application`
- **Business modules are out of Dignite Vault Extract**: downstream consumes decoupled via EventBus / MCP / REST; **no** new business-module dependency may be added inside Core

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

**Essence of mechanism (B)**: Dignite Vault Extract provides a generic "extract-by-schema" engine; Host / tenant configure the schema, and Dignite Vault Extract Core **presets no business field definitions** (contract amount / invoice number / tax amount, etc. are not hardcoded). Two independent single layers — `Document.TenantId` decides which layer's field definitions this document runs against; **never mix across layers**.

**Document field-extension hard constraints** (must hold before changing `Document.cs` / egress DTOs / `TextExtractionResult`; after such a change run the `abp-document-boundary-check` skill, or apply its checklist by hand):

1. **There is forever only one text-typed field: `Markdown`** (`Document.SetMarkdown` immutability is already enforced at the code level). Any derived text (Summary / Outline / SectionsJson) is projected on the consumer side via `MarkdownStripper.Strip`, **not persisted**. `Title` is an immutable display snapshot derived from Markdown, not a new text payload.
2. **Non-text fields are judged by "generic truth source vs. business-specific"**: generic ones (e.g. `PageBlocks`) may be added to `Document` (still requires an Issue to discuss shape); business-specific ones (contract amount / invoice number / ID-card name) are stored by downstream consumers in their own aggregate roots — **`Document` is not polluted**.
3. **Forbidden**: adding an **unconstrained** `Dictionary<string,object>` generic extension bag to `Document` / `TextExtractionResult` / event payloads; **forbidden** to introduce a parallel plain-text field.
   - *What "unconstrained" excludes* (#558): `Document.FlexFields` is a `FlexFieldDictionary` (a `Dictionary<string,object>`) and is **allowed**, because every key in it is a `Field.Name` backed by a persisted field definition with a declared field type — the schema lives in `VaultFields`, not in the bag. The rule targets a bag anyone can write an arbitrary key into, which is what makes a payload unreviewable and undeletable. A bag whose keys are enumerable from a definition table is the opposite. Adding a *second* bag, or widening this one to accept keys with no definition, is still forbidden.

> Full system-field table, text-extraction provenance (#210), and mechanism-B implementation form (field architecture v3, #558/#559/#562: the `Dignite.Abp.FlexFields` kernel — `Field : IFlexField` + the `Document.FlexFields` value bag + the derived `DocumentFlexFieldIndex`, with `FieldTypeName` + `Configuration` in place of a data-type enum): see `.claude/rules/field-architecture.md` (auto-loaded when editing Document / field code).

> Sub-document segmentation (a **container** document → derived sub-documents; also a concrete document → its embedded standalone documents, #494): the two-representation model (`DocumentSegment` ledger + derived `Document`), the two-`Kind` red line, and the field lifecycle contract — see `.claude/rules/sub-document-segmentation.md` (decision record #390, auto-loaded when editing Segmentation / `DerivedDocumentSpawner` / `ContainerMarker*` / `DocumentSegment*` code). Relevant to egress too: a container is suppressed from `DocumentReadyEto` (#346); only its sub-documents fire it, each carrying `OriginDocumentId`. A sub-document of either `Kind` carries **no `FileOrigin`** (#487 reverted #481's required-`FileOrigin` back to nullable): it is a Markdown slice seeded from its ledger row, and a consumer reaches its source by following `OriginDocumentId` to the parent's blob.

## Egress contracts

Three live egress channels: **REST API** (HTTP, generic programmatic access) / **MCP server** (Claude Desktop / Cursor / any MCP client) / **EventBus** (ABP DistributedEventBus, business systems / custom consumers); plus **Webhook** (legacy systems) — **planned, not yet implemented**.

**Multi-stage events** (thin payloads — ID + key metadata, downstream pulls details back): `DocumentUploadedEto` → `DocumentTextExtractedEto` → `DocumentClassifiedEto` → `DocumentReadyEto`; plus lifecycle events `DocumentDeletedEto` / `DocumentRestoredEto` / `DocumentPermanentlyDeletedEto` (orthogonal to the pipeline).

**Ready gate**: **only `DocumentReadyEto` is gated** — it fires once the document carries **no blocking review reason** (`ReviewReasonPolicy.Blocking`, the single declaration point): a confirmed type (classification confidence ≥ that type's `ConfidenceThreshold`, or manual confirmation), no suspected duplicate (#411), and field extraction not declined for an oversized body (#491). Documents that fail are still stored, early-stage events still fire, and they enter the operator review queue. Primary downstream consumers subscribe to `DocumentReadyEto` by default.

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
- **Bounded payloads** (#491): every text crossing an LLM boundary — into a prompt, or out on an LLM-facing egress — must have a ceiling. `Take(N)` bounds a result set's **rows**, never one payload's **bytes**. Where truncating the tail would silently corrupt the result (field extraction / segmentation: the thing being looked for can sit anywhere), the ceiling **gates** the call — skip it, raise a review signal, reach a terminal state, and **never rethrow into the background-job retry loop**. Where it would not (classification / title / cabinet suggestion / MCP document body), truncate via `TextTruncator.AtCharBoundary` (a raw `text[..n]` slice can split a surrogate pair) and **announce the cut**. Prompt ceilings are host configuration (`VaultExtractBehaviorOptions`); egress ceilings are compile-time `const` (`VaultExtractMcpConsts`) so the safety boundary cannot be widened at runtime

## ABP conventions

Verified against the code on 2026-09-21: nothing in `core/src` / `host/src` violates any of these today — keep it that way. (Generic ABP tutorial rules were removed from `.claude/rules/`; https://abp.io/docs/latest is the reference for anything not listed here.)

- **`virtual`**: every public/protected method on application services and domain services (`*Manager`) is `virtual`, because packaged modules are overridden by consumers. Helpers are `protected virtual`, not `private`.
- **Time**: `Clock.Now` / `IClock`, never `DateTime.Now` / `UtcNow`.
- **DI**: marker interfaces (`ITransientDependency` / `ISingletonDependency` / `IScopedDependency`), never `AddScoped` / `AddTransient` / `AddSingleton`. An interface with several implementers is auto-exposed only to the class whose name ends with the interface name (minus the `I`); anything else needs `[ExposeServices]` (#564).
- **Data access**: application services use `IRepository<T>` (or a custom repository, only where a custom query is needed) — never a `DbContext`. Child entities are reached through their aggregate root (the one documented exception is `DocumentPipelineRun`, see `background-jobs.md`).
- **No Minimal APIs, no MediatR**; no business logic in controllers — `HttpApi` and `Mcp` are thin adapters over application services.
- **Errors / i18n**: rule violations throw `BusinessException` with a namespaced code; user-facing text lives in `*.Domain.Shared/Localization/{Resource}/{lang}.json`. Error codes are frozen wire strings (see the naming convention above).
- **Async all the way**: no `.Result` / `.Wait()`.
- **Authorization**: permission-based, never hard-coded role checks. The documents domain has its own access-rule table and must not use `[Authorize]` — read `.claude/rules/authorization.md` before touching it.
- **Multi-tenancy**: never hand-write `TenantId` predicates (ABP's global filter does it); `CurrentTenant.Change(...)` only inside a `using`.
- **Tests**: ABP integration-style (test base + in-memory SQLite), Shouldly assertions, NSubstitute for LLM / OCR / blob doubles. One project: `dotnet test core/test/<Project>/<Project>.csproj` (the whole core suite runs in about a minute); Angular: `npx ng test vault-extract --watch=false` from `angular/`.

## Working rules

1. Development follows the conventions above and the path-scoped rules under `.claude/rules/` (they auto-load by `paths:` when you touch production code under `core/src` / `host/src`; when modifying ABP BackgroundJob / JobArgs you must read `.claude/rules/background-jobs.md`)
2. Do not configure middleware in core ABP modules — only in the host
3. **Decide whether an Issue is needed before changing**: changes touching channel boundaries (OCR pipeline / egress contracts / field architecture / document-type system / Markdown-first / security conventions), module boundaries, or Slice tasks — **stop first and tell the user to open a GitHub Issue before proceeding**; pure implementation-detail fixes (bug fixes, wording corrections) are recorded directly in the commit message
4. **Downstream-consumer questions**: business modules (contract / invoice management, etc.) are out of Dignite Vault Extract scope. For discussions involving downstream-consumer implementation, explicitly state it is out-of-scope; Dignite Vault Extract only guarantees stable egress contracts

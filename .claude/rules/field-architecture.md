---
description: "Dignite Vault Extract field architecture details: system common field table, type-bound field (mechanism B) implementation, field-extension judgment, document-type classification execution"
paths:
  - "**/Document.cs"
  - "**/Documents/**/*.cs"
  - "**/*Field*.cs"
  - "**/*DocumentType*.cs"
---

# Field Architecture Details (Dignite Vault Extract)

> Carried over from CLAUDE.md, auto-loaded when editing `Document` / field-definition / document-type code. CLAUDE.md keeps only the high-level split of the two field kinds, the essence of mechanism (B), the Document field-extension hard constraints, and the document-type two-independent-single-layer core constraints.

## System common fields (auto-produced by the Dignite Vault Extract pipeline, top-level typed columns)

Produced automatically by the Dignite Vault Extract pipeline + built-in LLM extraction, applicable to all documents, **requiring no schema configuration**. Stored as top-level typed columns on `Document` (strongly-typed LINQ + first-class indexes):

| Field | Source | Notes |
|------|------|------|
| `Title` | text-extraction pipeline | extracted from Markdown by `MarkdownTitleExtractor` |
| `Markdown` | text-extraction pipeline | Document's sole text payload (Markdown-first) |
| `DocumentTypeId` | classification pipeline | classification result (#207: internally associated by the immutable `DocumentType.Id`; external wire-format — REST / MCP / ETO — still outputs the `DocumentTypeCode` string, resolved by the read path joining `DocumentType`; a TypeCode rename does not cascade to this table) |
| `ClassificationConfidence` / `ReviewDisposition` / `ReviewReasons` / `RejectionReason` | classification pipeline + manual review | #284 dual-axis review model: `ReviewDisposition` is the operator disposition axis (NotReviewed / Confirmed / Rejected), `ReviewReasons` is the pending-reason flags axis (each bit maintained by exactly one pipeline stage); `RejectionReason` has a value only when Rejected (the operator's mandatory rejection note) |
| `LifecycleStatus` | pipeline orchestration | macro lifecycle status |
| `Language` | OCR / extraction stage | ISO 639-1 / IETF tag |

`FileOrigin` (Owned Entity) contains upload-time metadata such as `BlobName` (BlobStore Key) / `OriginalFileName` / `FileSize` / `ContentType` / `ContentHash`. There is no standalone "Filename / Size / Format" system field — the read path uses `d.FileOrigin.OriginalFileName` directly, etc. There is also `CabinetId` (nullable Guid, #194) — a manual filing/organization dimension set by operators, **orthogonal to the pipeline** (OCR / classification / field extraction neither read nor write it), not a pipeline-produced system field.

There is no standalone `PageCount` / `Summary` field — the former is a leaky abstraction (many documents have no page concept), and future page-aware citation uses the named extension `PageBlocks` rather than a single int; the latter is replaced by `Title` (a good Title is enough for UI list display).

## Document-type classification execution

- On upload, Dignite Vault Extract automatically runs the LLM classification prompt → categorizes within the layer the Document belongs to (exact match by `Document.TenantId`; the background path switches layer via `ICurrentTenant.Change` then goes through the generic `GetListAsync`)
- Low confidence or operator disagreement → the operator UI can correct manually
- After correction, downstream pipelines are re-triggered (e.g. the corresponding type's field extraction)

## Type-bound fields (mechanism B)

Type-bound fields must be attached under some document type, split into two layers by who defines them. **Two independent single layers — `Document.TenantId` decides which layer's field definitions this document runs against; never mix across layers**:

| Layer | Who defines | Scope (effective only for…) | Example |
|------|-------|------------------|------|
| **Host fields** | Host admin | **Host documents** (uploaded by Host itself) | e.g. Host adds "department / internal contract number" fields under its self-managed "Contract" type |
| **Tenant fields** | tenant admin (per-tenant) | **that tenant's documents** | e.g. a law-firm tenant adds "party / cause of action" fields under its self-managed "Case File" type |

**Key constraints**:

- **Host and tenant are two separate universes** — Host fields apply only to Host documents, tenant fields only to that tenant's documents; **there is no relationship of "a tenant field attached to a Host type" or "a Host field leaking into tenant documents"**
- **All business fields are self-configured** — in a multi-tenant SaaS scenario, a tenant attaches its own fields under its own type layer; the Host deployment layer serves only Host's own operational scenarios
- **Same-name fields across layers are allowed** (Host's `"amount"` field and tenant A's `"amount"` field are two independent rows, applying to documents of their respective TenantId)
- **Same TypeCode across layers is allowed** — a tenant may create a type with the same TypeCode as Host; the two are independent entities (distinguished by TenantId)

**Essence of mechanism (B)**: Dignite Vault Extract provides a generic "extract-by-schema" engine — Host or tenant configures the schema, and the engine extracts per the owning layer. Dignite Vault Extract Core **presets no business field definition** (contract amount / invoice number / tax amount, etc. are not hardcoded).

**Implementation form (field architecture v2)**:

- A single `FieldDefinition` entity carries both layers (`TenantId IS NULL` = Host field definition; `TenantId != null` = tenant field definition), with unique index `(TenantId, DocumentTypeId, Name)` (#207: internally associated by the immutable `DocumentTypeId`; `DocumentType.TypeCode` / `FieldDefinition.Name` may be renamed by admins without cascading to data rows)
- **No inheritance, and no initial registration in a Module**: both Host fields and tenant fields are created via `IFieldDefinitionAppService` CRUD (Host admin operates Host rows, tenant admin operates its own tenant rows)
- No cross-layer union: the admin view, the LLM classification candidate set, and field extraction all match a single layer by exact `Document.TenantId` (admin view / classification candidate set go through the generic `GetListAsync`; field extraction goes through `GetForExtractionAsync`)
- The classification stage schedules a single field-extraction run transactionally with classification completion (#527 §8: `DocumentPipelineJobScheduler`, before classification can derive Ready — no longer a delayed `DocumentClassifiedEto` handler), and `FieldExtractionService` matches a single layer of field definitions by exact `Document.TenantId` → one LLM call → writes the whole group via `Document.SetFields(...)` into the first-class `DocumentExtractedField` value collection (a child entity of the `Document` aggregate, one row per field value with typed columns; composite key `(DocumentId, FieldDefinitionId, Order)`, #207 + #212 — single-valued fields have `Order` always 0, multi-valued fields (AllowMultiple) have multiple rows per field with `Order` 0/1/2…). The egress DTO / MCP / REST `ExtractedFields` (`Dictionary<string, JsonElement>`) is assembled on the fly by the App / Mapper layer from these rows; the dictionary key (field name) is resolved through a soft-delete join to the current `FieldDefinition` (#206 / #207). Field-value queries use ordinary-column EF Core LINQ (Documents-anchored EXISTS, matching the child by `FieldDefinitionId`), portable across any relational database, no longer bound to SQL Server native `json` / `JSON_VALUE`
- Extraction completion uniformly publishes `FieldsExtractedEto` (thin payload with `FieldCount`; downstream can distinguish scenarios by the event payload's `TenantId`)

## Document field-extension judgment (full two axes)

The above principles are at the transient transport (`TextExtractionResult` / `OcrResult`) level; at the `Document` aggregate root (persistence layer, the truth source shared across downstream consumers) the rules are stricter. Two-axis judgment:

1. **Text-typed field: forever only one, `Markdown`.** This is the hard constraint of Markdown-first at the persistence layer (already enforced at the code level by `Document.SetMarkdown` immutability). Any derived text (Summary / Outline / SectionsJson) is projected on the consumer side via `MarkdownStripper.Strip` or a chunker, **not persisted**. `Title` is an immutable display snapshot derived from Markdown, not a new text payload; `RejectionReason` is the operator's manual note when rejecting review (#284, not document content)
2. **Non-text-typed field: judged by "generic truth source vs. business-specific"**:
   - **Generic truth source shared across downstream consumers** (e.g. `PageBlocks` for citation highlighting in any business, OCR Provider name/version for debugging) → may be added to `Document`, still requires an Issue to discuss shape
   - **Business-specific** (contract amount / invoice number / ID-card name / receipt line items) → stored by downstream business consumers in their own aggregate roots (downstream `Contract` / `Invoice` / `IdCardRecord`); **`Document` is not polluted**

This rule also answers "where do OCR out-of-band signals go" — they belong neither to downstream business (unrelated to any specific business) nor can be stuffed back into the Markdown string (which would break Markdown-first). They should be carried at the `Document` level, but **open a separate Issue per signal**, adding named strongly-typed nullable fields as needed; **forbidden** to use a `Dictionary<string, object>` generic extension bag.

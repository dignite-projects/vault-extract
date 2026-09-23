---
description: "Dignite Vault Extract field architecture details: system common field table, type-bound field (mechanism B) implementation, field-extension judgment, document-type classification execution"
paths:
  - "core/src/**/Document.cs"
  - "core/src/**/DocumentDto.cs"
  - "core/src/**/TextExtractionResult.cs"
  - "core/src/**/Fields/**/*.cs"
  - "core/src/**/*Field*.cs"
  - "core/src/**/DocumentTypes/**/*.cs"
  - "core/src/**/Classification/**/*.cs"
---

# Field Architecture Details (Dignite Vault Extract)

> Auto-loaded when editing `Document` / field / document-type code. CLAUDE.md holds the high-level split of the two field kinds, the essence of mechanism (B), the Document field-extension hard constraints, and the two-independent-single-layers rule for document types; this file holds the mechanics.

## System common fields (auto-produced by the pipeline, top-level typed columns)

Produced by the pipeline + built-in LLM extraction, applicable to all documents, **no schema configuration**. Stored as top-level typed columns on `Document` (strongly-typed LINQ + first-class indexes):

| Field | Source | Notes |
|------|------|------|
| `Title` | text-extraction pipeline | extracted from Markdown by `MarkdownTitleExtractor` |
| `Markdown` | text-extraction pipeline | the document's sole text payload (Markdown-first) |
| `DocumentTypeId` | classification pipeline | internally associated by the immutable `DocumentType.Id` (#207); the wire format (REST / MCP / ETO) still outputs the `DocumentTypeCode` string, resolved by the read path joining `DocumentType`, so a TypeCode rename does not cascade |
| `ClassificationConfidence` / `ReviewDisposition` / `ReviewReasons` / `RejectionReason` | classification pipeline + manual review | dual-axis review model (#284): `ReviewDisposition` = operator disposition (NotReviewed / Confirmed / Rejected); `ReviewReasons` = pending-reason flags, each bit maintained by exactly one pipeline stage; `RejectionReason` only when Rejected (the operator's mandatory note) |
| `LifecycleStatus` | pipeline orchestration | macro lifecycle status |
| `Language` | OCR / extraction stage | ISO 639-1 / IETF tag |

`FileOrigin` (owned entity) holds upload-time metadata (`BlobName` / `OriginalFileName` / `FileSize` / `ContentType` / `ContentHash`); there is no standalone Filename / Size / Format field — read `d.FileOrigin.*`. `CabinetId` (nullable Guid, #194) is a manual filing dimension set by operators, **orthogonal to the pipeline** — OCR / classification / extraction neither read nor write it.

There is no standalone `PageCount` (a leaky abstraction — many documents have no pages; page-aware citation uses the named extension `PageBlocks`) or `Summary` (`Title` covers list display).

## Document-type classification execution

- On upload the LLM classification prompt categorizes within the layer the Document belongs to (exact match by `Document.TenantId`; the background path switches layer via `ICurrentTenant.Change` then uses the generic `GetListAsync`).
- Low confidence or operator disagreement → the operator corrects manually; after correction downstream pipelines are re-triggered (e.g. that type's field extraction).

## Type-bound fields (mechanism B)

Host fields apply only to Host documents; tenant fields only to that tenant's documents; `Document.TenantId` decides which layer runs — see CLAUDE.md. Also true, and not stated there: **same-name fields across layers are allowed** (Host's `"amount"` and tenant A's `"amount"` are two independent rows); a tenant-field-on-a-Host-type or Host-field-leaking-into-tenant-documents relationship does not exist.

## Implementation form (field architecture v3, #558 / #559 / #562 / #564)

Built on the **`Dignite.Abp.FlexFields` kernel** (sibling repo `abp-modules/flex-fields`). The kernel supplies the mechanism — field types, the value bag, the derived index contract, key migration; Vault Extract supplies the policy: which types exist, how a field is scoped, and everything the pipeline does with a value.

### Where a field is defined

- A single `Field` entity (`Documents/Fields/Field.cs`, implementing the kernel's `IFlexField` + `IMultiTenant`) carries both layers: `TenantId IS NULL` = Host field, `TenantId != null` = tenant field.
- **Fields stay bound to one `DocumentType`** — deliberately *not* the kernel's other shape (a tenant-wide reusable field library plus a per-usage join). That is why `IsRequired` / `IsSearchable` / `IsUniqueKey` live directly on `Field`.
- **`Field.Name` is the machine contract key and the key its value is stored under in every document's bag**, so renaming rewrites every bag: change the definition, migrate the bags, let nothing synchronize the index in between. `FieldDefinitionAppService.UpdateAsync` does this through **`DocumentFieldValueMigrator`, not the kernel's `IFlexFieldValueMigrator<Document>`**: the kernel renames by name across every document the tenant has (one field name per host *type*), whereas a Vault Extract field is unique per `(TenantId, DocumentTypeId, Name)` — two types may each define `invoice_no`, and the unscoped walk would move the other type's values to a key no definition backs. Scoping to the field's own `DocumentTypeId` (`IDocumentRepository.GetIdsByDocumentTypeAsync`, which also traverses soft delete) is the fix. Do not reintroduce the kernel migrator here.
- **`Name`'s allow-list regex is Vault Extract's, not the kernel's** (the kernel validates no format). `Field.SetName` re-declares `FieldDefinitionConsts.NamePattern` because the value is concatenated raw into the LLM's schema message — a prompt-injection boundary, not a formatting preference.
- Uniqueness on `(TenantId, DocumentTypeId, Name)` is an **application-layer check** (`FieldDefinitionManager`), not a DB index — same cross-database rationale as `DocumentType` (#304). Every write path (create / rename / restore, including the cascade restore from `DocumentTypeAppService.RestoreAsync`) must route through it.
- **No inheritance, no module-startup registration**: Host and tenant fields alike are created through `IFieldDefinitionAppService` CRUD. No cross-layer union anywhere — the admin view, classification candidates and field extraction all match a single layer by exact `Document.TenantId`.
- The app service, DTO and REST route keep the names `IFieldDefinitionAppService` / `FieldDefinitionDto` / `/field-definitions` (they name the concept); only the entity behind them is `Field`.

### Field types

`Field.FieldTypeName` (a **registration key string**) plus `Field.Configuration` (type-specific settings). "What a field accepts" and "one value or many" are both properties of the type, not two independent switches.

| Key | Notes |
|---|---|
| `Text` | `Text.Mode` / `Text.CharLimit` / `Text.Placeholder` |
| `Number` | `Number.Decimals` / `Min` / `Max` / `Step` / `FormatSpecifier` |
| `Boolean` | `Boolean.Default` |
| `DateTime` | `Date` and `DateTime` are one type, told apart by `DateTime.InputMode` (Date / DateTime / Month). All three normalize to midnight on write so equality stays equality; **Month additionally pins the day to 1** (its day carries no information; egress emits year and month only). The mode→format mapping lives in one place, `DateTimeInputModeFormats.Format`, because the reader, the `ExtractedFields` writer and the export renderer all ask it; its Angular counterpart is `dateInputType` in `field-value-filter.model.ts` — change them together |
| `Select` | closed vocabulary. `Select.Options` is projected into the LLM extraction schema as a JSON-schema `enum`. Multi-valued when `Select.Multiple` |
| `CKEditor` | long text. `IndexValueType` is `null`, so "never indexed, never queryable" is structural rather than dependent on `IsSearchable`. Write `ContentFormat = Markdown` explicitly — the type's own default is `Html`, wrong for text extracted from a document |
| `Tags` | Vault Extract's own open-vocabulary multi-value type (`Dignite.Vault.Extract.FlexFields`), the complement of `Select`. Always a list |
| `Table` | the kernel's composite grid type (#625): one shared column schema (`TableConfiguration.Columns`, a list of `InlineFieldDefinition`) applied to every row, value `List<TableRow>`. `IndexValueType` is `null` like `CKEditor`. `IsMultiValue` is unconditionally `false` — its value is one composite scalar to the shared dispatchers. **Every row's cells sit wrapped under a `"values"` key** — `{"values": {<column>: <value>, ...}}`, the kernel's own `TableRow` JSON shape (mirrored by `ff-table-view` / `ff-table-control` on the Angular side) — in the extraction schema (`BuildExtractionSchema`), `TryRead`'s input parsing and `WriteJson`'s egress alike. A flat row is rejected by `TryRead`; the wrong shape looks plausible but every cell silently reads as absent in `ff-table-view`, with no error anywhere. `Matrix` is the kernel's other composite type; Vault Extract has no extension for it |

**"Is this field multi-valued" has two branches**: `Tags` always, and `Select` when its configuration says `Multiple`. Never test the type name alone — that silently mis-handles every multi-Select. Server-side the single answer is `IVaultExtractFieldTypeRegistry.IsMultiValue(fieldTypeName, configuration)`. There is no Angular twin: the UI uses `@dignite/ng.flex-fields` per-type controls, and the read-only display path (`formatExtractedFieldValue`) branches on the value's own shape (`Array.isArray`). Nothing client-side can answer the question for a field with *no value yet* — if that is ever needed, ask the server; do not re-add a client copy.

**Configuration enum values are written as numbers.** The kernel reads a configuration enum via `(int)(long)value`, and its fallback path deserializes with `JsonSerializerDefaults.Web`, which has no string-enum converter — a name silently falls back to the default.

### Field-type dispatch: the extension registry (#564)

Five call sites each do something different per field type: read/validate a raw value (`FlexFieldValueReader`), build the LLM extraction schema (`FlexFieldValueSchemaBuilder`), render a stored value to JSON on egress (`FlexFieldValueJsonWriter`), render an export cell (`ExportCellRenderer`), and canonicalize a value for the duplicate fingerprint (`FlexFieldFingerprintCalculator`). Adding a field type means adding **one** extension, not editing five if/else chains.

- **`IVaultExtractFieldTypeExtension`** (`Dignite.Vault.Extract.FlexFields`) bundles all the per-type operations — `FieldTypeName`, `IsMultiValue`, `TryRead`, `BuildExtractionSchema`, `WriteJson`, `RenderForExport`, `CanonicalizeForFingerprint`. One implementation per supported `Field.FieldTypeName`, in `Dignite.Vault.Extract.Application/Documents/Fields/FieldTypeExtensions/` (the concrete types need kernel field-type namespaces, which the zero-dependency `FlexFields` project cannot reference — it holds only the interface, the base class and the registry).
- **Implement `VaultExtractFieldTypeExtensionBase`, never the bare interface.** ABP exposes an implemented interface only when the class name ends with the interface name minus the leading `I`; `TextFieldTypeExtension` does not, so the interface would silently never be exposed — an empty `IEnumerable<IVaultExtractFieldTypeExtension>`, every `TryRead` quietly returning `false`, every extracted field rejected. The base class carries `[ExposeServices(...)]` and `ISingletonDependency` once. (See CLAUDE.md "ABP conventions" on `[ExposeServices]`.)
- **`IVaultExtractFieldTypeRegistry`** indexes every registered extension by name via constructor-injected `IEnumerable<IVaultExtractFieldTypeExtension>`. `Get` throws for an unregistered name (a programming error); `TryGet` is for call sites where "not registered" is legitimate (the fingerprint calculator treats an unrecognized type as "no usable value" — a partial key, never a thrown exception, never an untyped hash from `ToString()`). "Is this type supported" is `IsSupported` / `SupportedFieldTypeNames`, derived from what is registered — there is no separate allow-list.
- **The five dispatch classes stay static and take the registry as an explicit parameter**, so they remain pure functions unit-testable with a hand-built registry (`TestFieldTypeRegistry` in Application.Tests, or an inline array of extensions) instead of a DI container.
- **A composite type (`Table`) needs the registry to recurse into its columns, and cannot constructor-inject it** — the registry's constructor enumerates every extension, so that would be circular. `VaultExtractFieldTypeExtensionBase` carries `IAbpLazyServiceProvider LazyServiceProvider { get; set; }` (ABP property injection, as the kernel's own `FieldTypeBase` does), and `TableFieldTypeExtension.Registry` resolves lazily on first use. The property is annotated `[DisablePropertyInjection]`: without it ABP's property autowiring would eagerly resolve `IVaultExtractFieldTypeRegistry` while activating the very extension asking for it and reintroduce the cycle (verified: it stack-overflows).
- **`CanonicalizeForFingerprint` takes a `FieldConfigurationDictionary configuration`** (#625): `Table` needs its own column schema (order and each column's type) to canonicalize rows, and that schema lives in configuration. `FlexFieldFingerprintCalculator.Compute` passes `field.Configuration`; scalar implementations ignore it.

### Where a value lives

- **`Document.FlexFields`** (the kernel's `FlexFieldDictionary`, a JSON column) is **authoritative**, keyed by `Field.Name`. Written as a whole set through `Document.SetFlexFields(...)`; a null value drops its key. This is *not* the untyped extension bag CLAUDE.md forbids: every key is backed by a persisted `Field` with a declared type. Deliberately not ABP's `ExtraProperties`, so no other module can collide with a tenant's field names.
- **`DocumentFlexFieldIndex`** (`FlexFieldIndexBase<Document>`) is a **derived** typed pivot table, never authoritative — every row is re-derivable from the bag, so a type change or searchability change is repaired by `RebuildAsync`, not a data migration. Cascade-deleted with its document; `NumberValue` is mapped `decimal(38,6)` because the kernel leaves precision unset and EF's `decimal(18,2)` default would silently round.
- **Every write of the bag owes the index an `IFlexFieldIndexManager<Document>.SynchronizeAsync` in the same unit of work.** Miss one and that document silently stops matching its own field filters (the bag is correct, so every read *through the bag* still looks right). Exactly four such sites: `FieldExtractionService`'s extraction write, its no-definitions clearing path, `DocumentAppService.UpdateExtractedFieldsAsync`, and `DocumentClassificationBackgroundJob.CompleteRunAsync` (the container / classification-review retraction paths, which clear the bag via `Document.MarkAsContainer` / `RequestClassificationReview` without re-extracting).
- **`IsSearchable`** decides whether a field's values are decomposed into the index at all (default `true`). A type whose `IndexValueType` is null yields nothing regardless.

### The extraction path

The classification stage schedules a single field-extraction run transactionally with classification completion (#527 §8: `DocumentPipelineJobScheduler`, before classification can derive Ready — **not** a delayed `DocumentClassifiedEto` handler). `FieldExtractionService` then:

1. reads one layer of `Field` rows by exact `Document.TenantId` (`IFieldRepository.GetListAsync(documentTypeId)`);
2. builds the response schema from field type + configuration (`FlexFieldValueSchemaBuilder`);
3. makes one LLM call;
4. re-reads the definitions and applies the **in-flight guards** — a value whose field was deleted, **renamed**, or **retyped** while the LLM was in flight is discarded. The guards compare `FieldTypeName` *and* `Name` (the name is the bag key);
5. validates and converts each value with `FlexFieldValueReader` (one step, not validate-then-convert);
6. writes the whole group via `Document.SetFlexFields(...)` and synchronizes the index (#650: no event is published here — egress is `DocumentReadyEto`, fired by the caller's lifecycle round-trip once the run completes).

`FlexFieldValueReader` is the single validation gate, shared by extraction and the operator edit — the only difference is what happens on rejection (logged-and-skipped there, an interactive error here). The extraction workflow deliberately does **not** validate: it hands the raw `JsonElement` through, because a second gate could only diverge from the reader.

### The read and query paths

- The egress `ExtractedFields` (`Dictionary<string, JsonElement>`) is assembled on the fly by `DocumentAppService.AssembleExtractedFields` from the bag, rendered per field type by `FlexFieldValueJsonWriter`. Reference resolution runs under `Disable<ISoftDelete>` so an archived field a historical document still holds a value for keeps resolving to a name.
- Field-value **filtering** goes through `DocumentFieldQueryResolver` → `IFlexFieldQueryExecutor<Document>`, which composes a subquery against the index; the value type comes from `IFieldType.IndexValueType`. An unknown field name **loud-fails** (`BusinessException`), never a silent empty result; so does filtering on a non-indexable type.
- **Presence is tested by name, not by id**: `document.FlexFields.ContainsKey(field.Name)`, for the missing-required-fields reason and its detail projection alike. An id-against-value-rows test compiles fine and reports every required field of every document as missing.

### Duplicate detection (#411)

`FlexFieldFingerprintCalculator` hashes the **unique-key fields' values read from the bag**, in a canonical order.

### Changing a field's type

Refused when the field already holds values (fail closed): the values would stay in the bag untouched, but nothing would render or index them under the new type — a silent disappearance. Narrowing a multi-value field counts as a type change.

"Does any document hold this field" has no cheap general answer: the bag is an opaque JSON column and no provider can push a bag-key predicate into it. `IDocumentRepository.AnyFlexFieldValueAsync` answers it two ways — from the derived index by `FieldId` when the field is indexable and searchable (exact, one lookup), and by paging the type's documents and testing the key in memory when it is not (long text, or searchability off). Keep that fallback confined to those cases: it is bounded only by the type's document count.

### v2 is gone (#593)

The v2 entities (`FieldDefinition`, `DocumentExtractedField`), their tables, the one-shot migrator and the v2 value-query path were removed in #593. What survives is deliberate: `FieldDefinitionToFieldMapper` (its `FieldDataType`-based overloads still serve `FieldDraftSuggestionAppService` and `DocumentTypePackV1Upconverter`) and the v2-era constants / `FieldDataType` enum that live v3 code still references. Do not reintroduce a v2 read or write.

`DocumentFieldValidationWarning.FieldDefinitionId` keeps its column name (it is on the wire, in `ResolveFieldValidationWarningsInput` and the review-reason detail DTO) but its FK points at `VaultFields` (#562).

## Document field-extension judgment

The hard constraints are in CLAUDE.md ("Document field-extension hard constraints", "Markdown-first"): only one text field `Markdown`; non-text fields split by "generic truth source vs. business-specific"; no unconstrained `Dictionary<string,object>` bag (`Document.FlexFields` is the one allowed bag, #558, because every key is backed by a `Field` definition). Worked examples for the second axis:

- **Generic, shared across consumers** → may be added to `Document`, still with an Issue to discuss shape: `PageBlocks` (citation highlighting in any business), OCR provider name/version (debugging).
- **Business-specific** (contract amount / invoice number / ID-card name / receipt line items) → stored by downstream consumers in their own aggregate roots (`Contract` / `Invoice` / `IdCardRecord`); **`Document` is not polluted**.
- **OCR out-of-band signals** belong to neither downstream business nor the Markdown string (that would break Markdown-first): carry them on `Document` as named, strongly-typed, nullable fields, **one Issue per signal**.
- `Title` is a display snapshot derived from Markdown (replaced only together with it, by a re-parse, #660); `RejectionReason` is the operator's manual note (#284) — neither is a new text payload.

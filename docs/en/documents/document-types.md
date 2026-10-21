```json
//[doc-seo]
{
    "Description": "Define document types and their fields in Dignite Vault Extract: Host and tenant layers, field types, extraction prompts, duplicate detection, validation warnings and type packs."
}
```

# Document Types and Fields

A **document type** is what classification sorts documents into, and the container of the **fields** extracted from documents of that type. Vault Extract ships **no built-in types and no business fields**: an administrator defines every type and every field, and the channel extracts by that schema. A contract amount or an invoice number is a field you define, not something the module knows about.

> A business system that needs more than field values (an approval workflow, a contract record) builds it on its own side from the [egress contracts](../egress/integration-events.md). Vault Extract stays a channel.

## Host and Tenant Layers

Types and fields live in two separate **layers**:

- The **Host layer** (`TenantId` is null) is defined by the Host administrator and applies only to the Host's own documents.
- Each **tenant layer** is defined by that tenant's administrator and applies only to that tenant's documents.

The layers never mix. A Host type is **not** visible to tenants, and a tenant's types are invisible to other tenants. A document is classified against, and extracts fields for, the types of its own layer only (decided by the document's `TenantId`). The same type code, or the same field name, may exist in several layers as independent rows, so a downstream consumer must route by the **`(TenantId, DocumentTypeCode)`** pair, never by the code alone.

## Document Types

Types are managed on **Vault Extract → Document Types** (`IDocumentTypeAppService`).

| Property | Constraints | Used for |
| --- | --- | --- |
| `TypeCode` | Required, at most 128 characters, letters, digits, `_` and `-` in dot-separated segments (for example `invoice.standard`). Unique in its layer | The machine identity of the type on every egress (`DocumentTypeCode`). Documents reference the type by id, so a code can be renamed without touching them; downstream consumers keyed on the old code must follow |
| `DisplayName` | Required, at most 128 characters, no control characters | Shown in the UI and sent to the classifier as the candidate's name |
| `Description` | Optional, at most 512 characters, no control characters | Sent to the classifier with the name. Describe what distinguishes this type from its neighbors |
| `ConfidenceThreshold` | `0` to `1`, default `0.7` | A classification at or above it is accepted automatically; below it the document goes to review |
| `Priority` | Integer | Candidate order in the classification prompt (higher first). When a layer has more types than `MaxDocumentTypesInClassificationPrompt` (default 50), the lowest-priority ones are left out |
| `DuplicateScope` | `Layer` (default) or `Uploader` | Where [duplicate detection](#duplicate-detection) looks |

While typing a display name, the editor suggests a code through `ISlugSuggestionAppService`, which asks the LLM for an English, URL-safe slug.

### Deleting and Restoring

Deleting a type is refused (`Extract:DocumentTypeInUse`) while any document references it, recycle-bin documents included. Otherwise the type and its fields are soft-deleted. A deleted type still holds its code: a new type cannot reuse it until the deleted one is gone for good.

Restoring a type restores its fields with it (a field whose name is now taken by an active field is skipped) and re-checks the [prompt budget](#prompt-budget). Deleting or restoring a type never touches documents.

## Fields

A type's fields are managed on its **Fields** page (`IFieldDefinitionAppService`). Each field belongs to exactly one type.

| Property | Constraints | Used for |
| --- | --- | --- |
| `Name` | Required, 1 to 64 letters, digits, `_` or `-`. Unique within the type | The key the value is stored and served under (`extractedFields.<name>`), and the name in field filters. Treat it as an API contract |
| `DisplayName` | Required, at most 128 characters | Column header in the UI and in [Data Download](../egress/data-download.md) |
| `Description` | Optional | **The extraction instruction** sent to the LLM for this field: what to look for, the expected format, and any validation rule (see [Validation Warnings](#validation-warnings)) |
| `FieldTypeName` | One of the [field types](#field-types); default `Text` | What the field accepts, how it is validated, rendered and queried |
| `Configuration` | Depends on the field type | Type-specific settings (options, limits, input mode, columns) |
| `DisplayOrder` | Integer | Column order in the document list and in downloads (ties broken by `Name`) |
| `IsRequired` | Default `false` | A missing value raises the non-blocking `MissingRequiredFields` review reason |
| `IsSearchable` | Default `true` | Whether the values are written to the query index, so they can be [filtered](../egress/rest-api.md#listing-and-filtering-documents). Not available on long text and table fields |
| `IsUniqueKey` | Default `false` | Part of the [duplicate detection](#duplicate-detection) key. Not available on long text and table fields |

### Field Types

| Type | Value | Configuration | Filterable |
| --- | --- | --- | --- |
| `Text` | One string | `Text.Mode`, `Text.CharLimit` (enforced as the maximum length), `Text.Placeholder` | Equality |
| `Number` | One number | `Number.Decimals`, `Number.Min`, `Number.Max`, `Number.Step`, `FormatSpecifier` (display settings; extraction checks only that the value is a number) | Equality or range |
| `Boolean` | `true` / `false` | `Boolean.Default` | Equality |
| `DateTime` | A date, date-time or month, chosen by `DateTime.InputMode` (`Date`, `DateTime`, `Month`) | `DateTime.InputMode`, `DateTime.Min`, `DateTime.Max` (enforced) | Equality or range |
| `Select` | One option, or several when `Select.Multiple` is set | `Select.Options` (the closed vocabulary, sent to the LLM as an enum), `Select.Multiple`, `Select.NullText`, `Select.Size` | Equality |
| `Tags` | A list of free strings | `Tags.MaxCount` (default 100), `Tags.MaxLength` (default 256), `Tags.Placeholder` | Equality |
| `CKEditor` | Long text, stored as Markdown | `CKEditor.Mode`, `CKEditor.ContentFormat`, … | No |
| `Table` | Rows sharing one column schema; each column is itself a field definition | `Table.Columns` | No |

Field types come from the [`Dignite.Abp.FlexFields`](https://github.com/dignite-projects/abp-modules) kernel, plus Vault Extract's own `Tags` type. `GetFieldTypesAsync` returns the types this deployment supports and whether each is indexable. Values are served as `yyyy-MM-dd` (`Date`), `yyyy-MM-ddTHH:mm:ss` (`DateTime`, no offset) and `yyyy-MM` (`Month`, stored as the first day of the month).

### Changing a Field

- **Renaming** is allowed. Every document of the type has its stored value moved to the new name, but downstream consumers and saved filters that use the old name must change.
- **Changing the field type** (or a table's columns) is refused while any document holds a value for the field (`Extract:FieldDefinitionDataTypeChangeNotAllowed`): the old values would stay stored but nothing could read them under the new type.
- **Turning `IsSearchable` on or off** rebuilds the query index of the type in the background.
- **Turning `IsUniqueKey` on or off** applies to documents as they are extracted again; existing documents keep their current duplicate verdict until then.
- **Deleting** a field soft-deletes it and stops extracting it. Documents keep their stored values, which REST and MCP still return in `extractedFields` (the read path resolves deleted fields on purpose, so historical documents stay readable), but the field leaves [downloads](../egress/data-download.md) and field filters. Restoring the field (its type must be active and its name free) brings it back.

Changing a field does not re-extract existing documents. Run a [batch field re-extraction](../pipeline/reprocessing.md) when you want them to pick up the change.

### Prompt Budget

Every field's `Description` is sent with every field-extraction call of its type, so the **total** across a type's active fields is capped by `Vault:ExtractBehavior:MaxFieldSchemaPromptLength` (default 32,000 characters). A create, update, restore or import that would exceed it is refused (`Extract:FieldDefinitionSchemaPromptBudgetExceeded`). One long prompt is fine; it is the sum that is bounded.

### AI Drafting Helpers

The field editor offers two LLM helpers. Both fail open: on a timeout or a provider error the editor keeps what you typed.

| Helper | Service | What it does |
| --- | --- | --- |
| Draft a field | `IFieldDraftSuggestionAppService.DraftAsync` | Turns a plain-language request (up to 4,000 characters) into a draft: display name, name (for a new field), field type, configuration and the required flag |
| Polish a prompt | `IFieldPromptPolishAppService.PolishAsync` | Rewrites a field's extraction instruction into clear Markdown |

Both need `VaultExtract.FieldDefinitions.Create` or `.Update` and use the structured LLM client.

## How Fields Are Extracted

After classification assigns a type, one **field-extraction** run sends the document's complete Markdown and the type's schema to the LLM in a single call. The schema is built from each field's type and configuration (for example a `Select` field's options become an enum), with each field's `Description` as its instruction.

Every returned value is validated against its field type before it is stored; an invalid value is dropped and logged. A value whose field was renamed, retyped or deleted while the call was running is discarded. The accepted values replace the document's previous values as a whole.

The Markdown is never truncated for field extraction, because a field can sit anywhere in a document. A document longer than `MaxFieldExtractionMarkdownLength` (default 200,000 characters) is not sent at all: it gets the blocking `FieldExtractionIncomplete` review reason, and an operator enters the values by hand and confirms them. See [AI Provider](../configuration/ai-provider.md#cross-cutting-llm-behavior-extractbehavior).

Operators can correct any value on the document's detail page. A correction is validated the same way, replaces the whole set, and re-fires `DocumentReadyEto` for a document that stays Ready.

## Duplicate Detection

Duplicate detection is **opt-in per type**: mark the fields that identify a document (an invoice number and an issuer, for example) as **unique keys**. After extraction, Vault Extract hashes the unique-key values into a fingerprint; another live document of the same layer and type with the same fingerprint makes this document a **suspected duplicate**, a blocking review reason. A document with a unique-key field missing has no fingerprint and is never flagged.

The type's `DuplicateScope` decides which documents are compared:

| Scope | Compares against |
| --- | --- |
| `Layer` (default) | Every document of the type in the layer, whoever uploaded it |
| `Uploader` | Only documents uploaded by the same user. Documents uploaded without a user share one group |

Changing the scope re-evaluates the type's existing documents in the background (no LLM call), which can release documents to Ready or send others to review. The type editor previews the effect first (`GetDuplicateScopePreviewAsync`: how many documents would be flagged and how many cleared).

An operator resolves a suspected duplicate by **allowing** it (`AllowDuplicateAsync`), which stays in force until the document's key values change, or by deleting it. Deleting a type's last unique-key field clears the fingerprints and flags of its documents.

> This is separate from the upload check, which refuses the exact same file uploaded twice by the same user. See [Documents and Review](documents.md#uploading).

## Validation Warnings

A field's `Description` can state a validation rule ("the tax amount must equal 10% of the subtotal"). When an extracted value breaks it, the LLM returns the value **and** a warning. The value is stored; the warning raises the blocking `FieldValidationWarning` review reason and is shown on the document's detail page (`ReviewReasonDetails`). Warnings never appear on search results, downloads or events.

An operator dismisses warnings after checking the values (`ResolveFieldValidationWarningsAsync`); the reason clears when none remain. A clean re-extraction, a type change or deleting the field also clears them. Correcting a value does **not** clear its warning by itself, so the operator's dismissal stays an explicit act.

## Importing and Exporting Types

A **type pack** is a JSON description of one type and its fields, without ids or tenant, so a schema can move between layers or deployments.

- **Export**: `GET /api/vault-extract/document-type-packs/{id}` for one type, `GET /api/vault-extract/document-type-packs` for every type of the layer.
- **Import**: `POST /api/vault-extract/document-type-packs/import` with 1 to 100 packs and a mode:
  - `CreateOrUpdate` (default): creates missing types and fields, overwrites existing ones (matched by type code and field name).
  - `CreateOnly`: creates missing ones and skips existing ones.

Import never deletes anything. Every pack is validated (version, field types, prompt budget) before anything is written, and a type or field whose code or name belongs to a soft-deleted row is refused. Packs written by older versions (pack version 1, with `dataType` instead of `fieldTypeName`) are converted on import. Importing needs `VaultExtract.DocumentTypes.Create` and `VaultExtract.FieldDefinitions.Create`, plus the matching `.Update` permissions to overwrite.

```json
{
  "version": 2,
  "typeCode": "invoice.standard",
  "displayName": "Invoice",
  "description": "A supplier invoice requesting payment.",
  "confidenceThreshold": 0.7,
  "priority": 10,
  "duplicateScope": 0,
  "fields": [
    {
      "name": "invoice_no",
      "displayName": "Invoice number",
      "description": "The invoice number printed on the document.",
      "fieldTypeName": "Text",
      "configuration": {},
      "displayOrder": 0,
      "isRequired": true,
      "isSearchable": true,
      "isUniqueKey": true
    }
  ]
}
```

## Permissions

| Permission | Allows |
| --- | --- |
| `VaultExtract.DocumentTypes` | Viewing types, including deleted ones, and exporting packs |
| `VaultExtract.DocumentTypes.Create` / `.Update` / `.Delete` | Creating, editing, deleting and restoring types |
| `VaultExtract.DocumentTypes.ManagePermissions` | Granting per-type permissions ([Per-Document-Type Permissions](../configuration/document-type-permissions.md)) |
| `VaultExtract.FieldDefinitions` | Viewing deleted fields |
| `VaultExtract.FieldDefinitions.Create` / `.Update` / `.Delete` | Creating, editing, deleting and restoring fields; the AI drafting helpers |

The list of a layer's active types and fields is also readable with `VaultExtract.Documents`, because the document pages need it. A type's schema is not confidential.

## See Also

- [Classification](../pipeline/classification.md)
- [Documents and Review](documents.md)
- [Reprocessing](../pipeline/reprocessing.md)
- [REST API](../egress/rest-api.md)
- [Data Download](../egress/data-download.md)

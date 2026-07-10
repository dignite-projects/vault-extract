# Data Download

Data Download is Dignite Vault Extract's **file-based egress** — the last mile to downstream systems that have no API and can only ingest files (Yayoi, freee, mid-market Yonyou, and similar accounting packages that import CSV). It sits alongside the programmatic egresses (REST / MCP / EventBus, with Webhook planned) but serves a different audience: **a human who downloads a file and imports it somewhere else.**

That audience is the whole reason it exists. REST, MCP, and EventBus each need a technical consumer on the other end. A 税理士 sitting at the operator UI has no other self-serve egress, and it is also the deterministic, no-LLM-in-the-loop way to get the exact extracted values — a safety property that matters precisely *because* an AI client could otherwise transform them.

**Industry-format conversion (弥生 仕訳 layout, account-code mapping, tax split) is not done here.** It lives downstream, in a consumer-side skill or business system. This surface only serializes the channel's own structured data. That boundary is the point: `CLAUDE.md` forbids pre-baked vertical templates, and a dumb serializer is what makes that rule livable.

## How it works

```
POST /api/vault-extract/documents/export
  { documentTypeCode, format, …the document list's filters }
        │
        │  ambient IMultiTenant filter; narrowed to documentTypeCode
        │  matched rows > MaxExportDocumentCount ? → fail (Extract:ExportDocumentLimitExceeded)
        ▼
  4 fixed system columns + one column per live FieldDefinition of that type (DisplayOrder, Name)
        │
        ▼
  ExportFileBuilder ──► CSV / XLSX stream, named {typeCode}-{yyyyMMdd-HHmmss}.{ext}
```

### Columns

- **Fixed system columns** — always emitted, never configurable: `LifecycleStatus`, `ReviewStatus`, `ReviewReasons`, `Title`. These are the channel's stable metadata contract.
- **Field columns** — one per **live** `FieldDefinition` of the requested type, in `DisplayOrder` (ties broken by `Name`, so the order is total). Headers are the field's `DisplayName`, so renaming a field follows through automatically. Values come from the document's `DocumentExtractedField` rows, rendered from the typed column per the field's `DataType`; a multi-value field joins its values by `Order` with `"; "`.

There is **no saved column projection.** #499 deleted the export-template layer: a template persisted only "which of this type's fields, in what order", and ordering was already owned by `DisplayOrder` — the same axis the operator list renders its columns by. Two axes over the same fields could disagree, so there is now one. If you want a different column order, change `FieldDefinition.DisplayOrder`; the list and the file move together.

> **Archived fields.** Columns come from *live* field definitions. If a field definition is soft-deleted, its column disappears from the file even for documents that still hold a value for it. Restore the definition to get the column back.

### Scope

The export takes exactly the filters the operator document list can express — lifecycle status, cabinet, creation-time range, needs-review, sub-documents of a source, and extracted-field-value filters — so the file is the view. `documentTypeCode` is **required**: extracted fields are type-scoped, and the columns *are* that type's field definitions, so a mixed-type view has nothing to emit. An unknown type code fails loudly rather than returning a header-only file.

## Formats

- **CSV** — UTF-8 with BOM (so Excel renders CJK correctly), RFC-4180 quoting. The mainstream format for accounting-software import.
- **XLSX** — generated with [ClosedXML](https://github.com/ClosedXML/ClosedXML) (MIT). For human review or systems that ingest `.xlsx`.

JSON file export is intentionally **not** offered — programmatic consumers should pull JSON over the REST API rather than download a file.

## Triggering a download

- **Operator UI** — filter the document list to what you want, pick a single document type, then **Download → CSV / XLSX** in the toolbar. The download carries the list's current filters.
- **API** — `POST /api/vault-extract/documents/export` with `ExportDocumentsInput`.

> EventBus-triggered export is **not** offered: a subscriber that already consumes the EventBus has the structured data — having the channel generate a file and hand it back closes no loop.

## Limits & safety

- **Tenant isolation** is enforced by ABP's ambient `IMultiTenant` global filter on the `Documents` query, per the `CLAUDE.md` security conventions.
- **Per-export document cap** (`DocumentExportConsts.MaxExportDocumentCount`, default 10000): if the filters match more rows than the cap, the export **fails** (`Extract:ExportDocumentLimitExceeded`) rather than silently truncating — for accounting data, dropping vouchers is more dangerous than an error. Narrow the filter.
- **Permission**: `VaultExtract.Documents.Export`. (The old `VaultExtract.Documents.Templates.*` keys were removed with the template layer; grants naming them are inert.)

## Example: composing a freee-style import CSV

Dignite Vault Extract ships nothing freee-specific. You compose the format from your own type-bound fields.

Suppose a tenant has an `invoice` document type with tenant fields `issue_date`, `amount`, `partner_name` (defined via `IFieldDefinitionAppService`, extracted automatically after classification). Set their `DisplayOrder` to 0 / 1 / 2 and their `DisplayName` to the headers you want. At month-end, filter the document list to the invoices you want and download.

The CSV's header row is the four fixed system columns followed by your fields in `DisplayOrder`; a downstream importer that wants only the business columns ignores or strips the leading system columns.

The same mechanism produces a Yayoi journal layout, a Yonyou voucher CSV, or any other ingest format: define the fields, name them, order them. If a target format needs a value the channel doesn't capture, add a `FieldDefinition` for it. The channel still doesn't *know* what freee is — it just lets you describe the shape.

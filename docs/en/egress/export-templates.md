# Export Templates

Export templates are Dignite Vault Extract's **file-based egress** — the "last mile" to downstream systems that have no API and can only ingest files (Yayoi, freee, mid-market Yonyou, and similar accounting packages that import CSV). They sit alongside the programmatic egresses (REST / MCP / EventBus, with Webhook planned) but serve a different audience: a human who downloads a file and imports it into another system.

A template is **per-tenant configuration** following the same two-layer model as `DocumentType` and `FieldDefinition`. Dignite Vault Extract ships **no built-in templates** — there is no industry vertical schema baked in. The export engine only does **field projection → rename → ordering → serialization**, with **zero business transformation** (no tax calculation, no account-code mapping, no currency conversion). Business formats are something tenants *compose* out of a template; Dignite Vault Extract enables them rather than doing them.

This is the concrete demonstration of the channel's "enable, don't do" philosophy: the OUT-of-scope rule in `CLAUDE.md` forbids pre-baked vertical templates, and this feature is exactly the mechanism that makes that rule livable.

## How it works

```
ExportTemplate (Name, Format, DocumentTypeId, Columns[])      // DocumentTypeId required (#207)
        │
        ▼
ExportAsync(TemplateId, DocumentIds? | LifecycleStatus filter)
        │   ambient IMultiTenant filter + narrow to template's type  ──►  count > limit ? → fail (ExportDocumentLimitExceeded)
        ▼
documents ──► fixed system columns + per-extracted-column projection ──► ExportFileBuilder ──► CSV / XLSX stream
```

The output is **fixed system fields first, then the template's extracted-field columns** (#207):

- **Fixed system fields** — always emitted, not configurable: `LifecycleStatus`, `ReviewStatus`, `Title`. These are Dignite Vault Extract's stable metadata contract; tenants don't configure them the way they configure business fields.
- **Extracted-field columns** — each `ExportColumn` references one type-bound field. Internally the column stores the **immutable `FieldDefinitionId`** (so renaming `FieldDefinition.Name` doesn't break the template — #207); the API submits and returns the field `Name`, resolved at save/read time against the template's document type. The value comes from the document's `DocumentExtractedField` row matched by `FieldDefinitionId` (issue #206), rendered from its typed column per the field's `DataType`. Host fields and tenant fields need no distinction here — a document only ever carries one layer's extraction result (field architecture v2's "two layers mutually exclusive"), so fields never collide.

Each `ExportColumn` (API shape) carries `{ FieldName, ColumnName, Order }`. `ColumnName` is the header text written to the file (Unicode is allowed, so non-ASCII headers work; control characters are rejected). `Order` sorts the extracted columns ascending.

Because every column is a type-bound field, a template is inherently type-scoped: `DocumentTypeId` (submitted/returned as `DocumentTypeCode`) is **required**, and the export only ever covers documents of that type.

## Formats

- **CSV** — UTF-8 with BOM (so Excel renders CJK correctly), RFC-4180 quoting. The mainstream format for accounting-software import.
- **XLSX** — generated with [ClosedXML](https://github.com/ClosedXML/ClosedXML) (MIT). For human review or systems that ingest `.xlsx`.

JSON file export is intentionally **not** offered — programmatic consumers should pull JSON over the REST API rather than download a file.

## Triggering an export

Two paths, both backed by the same `IExportTemplateAppService.ExportAsync`:

- **Operator UI** — pick a template, select documents (checkbox) or apply a filter, download.
- **API** — `POST` `ExportDocumentsInput { TemplateId, DocumentIds? | LifecycleStatus }`. When `DocumentIds` is non-empty it wins; otherwise the `LifecycleStatus` filter applies. The document type is **not** an input — it's fixed by the template (#207).

> EventBus-triggered export is **not** offered: a subscriber that already consumes the EventBus has the structured data — having Dignite Vault Extract generate a file and hand it back closes no loop.

## Limits & safety

- **Tenant isolation** is enforced by ABP's ambient `IMultiTenant` global filter on the `Documents` query (issue #206), per the `CLAUDE.md` security conventions.
- **Per-export document cap** (`ExportTemplateConsts.MaxExportDocumentCount`, default 10000): if the selection matches more rows than the cap, the export **fails** (`ExportDocumentLimitExceeded`) rather than silently truncating — for accounting data, dropping vouchers is more dangerous than an error. Narrow the filter or select fewer documents.
- Permissions: managing templates needs `Extract.Documents.Templates.*`; running an export needs `Extract.Documents.Export`.

## Example: composing a freee-style import CSV

Dignite Vault Extract ships nothing freee-specific. You compose the format from your own type-bound fields.

Suppose a tenant has an `invoice` document type with tenant fields `issue_date`, `amount`, `partner_name` (defined via `IFieldDefinitionAppService`, extracted automatically after classification). Configure one template referencing those fields by name:

| FieldName | ColumnName | Order |
|---|---|---|
| `issue_date` | `Issue Date` | 0 |
| `amount` | `Amount` | 1 |
| `partner_name` | `Partner` | 2 |

Set `Format = Csv` and `DocumentTypeCode = invoice` (required — the template is type-scoped). At month-end, filter the document list to the invoices you want and export. The CSV's header row is the **three fixed system columns** (`LifecycleStatus,ReviewStatus,Title`) followed by your configured columns (`Issue Date,Amount,Partner`); a downstream importer that wants only the business columns ignores or strips the leading system columns.

The same mechanism produces a Yayoi journal layout, a Yonyou voucher CSV, or any other ingest format: define the fields, map them to the target column names, pick the order. If a target format needs a value Dignite Vault Extract doesn't capture, add a `FieldDefinition` for it — the channel still doesn't *know* what freee is, it just lets you describe the shape.

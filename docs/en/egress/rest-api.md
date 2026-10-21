```json
//[doc-seo]
{
    "Description": "Call the Dignite Vault Extract REST API: authentication, uploading, reading documents and their Markdown and field values, list filters, and the endpoint reference."
}
```

# REST API

The REST API is Vault Extract's general programmatic egress: a business system or script uploads documents, reads their Markdown and field values, and acts on them. The operator UI uses the same API. For an AI client, prefer the [MCP server](mcp-server.md); for change notifications, subscribe to the [integration events](integration-events.md) and pull details from here.

The API is provided by the `Dignite.Vault.Extract.HttpApi` package. Every route starts with **`/api/vault-extract/`**. The running host documents every endpoint in Swagger UI at `/swagger`.

## Authentication

The API uses the host's OpenIddict Bearer tokens (scope and audience `VaultExtract`), like every ABP HTTP API. An unattended system that only reads obtains a token with the OAuth **client-credentials** grant from a client registered in the host's OpenIddict application store; interactive tools use the authorization code flow. A tenant's caller includes the tenant through ABP's usual tenant resolution (for example the `__tenant` header) or the token's tenant claim.

> **Known limitation:** uploading currently needs a token that carries a **user**. Each document records its uploader's name, and a client-credentials token has none, so the upload fails after the file has been stored. Until this is fixed, upload with a dedicated service user (for example through the password or authorization code flow).

Every call also needs the `VaultExtract.Enable` feature for the caller's tenant and the permissions of the operation. Document permissions are per type and per owner; see [Per-Document-Type Permissions](../configuration/document-type-permissions.md). Errors use ABP's standard error response with a namespaced code (`Extract:...`).

> A .NET caller can use the C# client proxies in `Dignite.Vault.Extract.HttpApi.Client` (`VaultExtractHttpApiClientModule`, remote service name `VaultExtract`) instead of raw HTTP; see ABP's [Static C# API Client Proxies](https://abp.io/docs/latest/framework/api-development/static-csharp-clients).

## Uploading a Document

```bash
curl -X POST https://your-host/api/vault-extract/documents/upload \
  -H "Authorization: Bearer $TOKEN" \
  -F "File=@invoice.pdf;type=application/pdf" \
  -F "DocumentTypeId=3a1f2c4e-0000-0000-0000-000000000000"
```

`DocumentTypeId` and `CabinetId` are optional. The response is the new document (`DocumentDto`) with status `Uploaded`; processing continues in the background. The accepted file types, the size limit and the upload checks are in [Documents and Review](../documents/documents.md#uploading).

## Reading a Document

`GET /api/vault-extract/documents/{id}` returns a `DocumentDto`:

| Property | Description |
| --- | --- |
| `id`, `tenantId` | Identity and layer |
| `documentTypeCode` | The type's code, or `null` while unclassified (and for a container) |
| `lifecycleStatus`, `reviewDisposition`, `reviewReasons`, `requiresReview` | State; see [Documents and Review](../documents/documents.md) |
| `reviewReasonDetails`, `rejectionReason` | Why the document needs review, or why it was rejected |
| `classificationConfidence` | `0.0` to `1.0` |
| `title`, `language` | Generated title and detected language |
| `markdown` | The document's text, the one text payload of the channel |
| `extractedFields` | Field values keyed by field `name` (see below), or `null` |
| `extractionIsComplete`, `extractionIncompleteReason` | Whether text extraction captured the whole document |
| `fileOrigin` | Upload metadata: `originalFileName`, `contentType`, `fileSize`, `uploadedByUserName` (`null` for a sub-document) |
| `cabinetId`, `originDocumentId`, `isContainer` | Filing and [sub-document](../documents/sub-documents.md) relations |
| `creationTime` | Upload time |
| `rights` | What the caller may do: `canRead`, `canEdit`, `canReview`, `canDelete`, `canRestore`, `canRetry` |

A caller who may not read the document gets only `id` and `rights` back.

`extractedFields` values keep their type: a string for `Text` and single `Select`, a number, a boolean, a date string (`yyyy-MM-dd`, `yyyy-MM-ddTHH:mm:ss` or `yyyy-MM`, per the field's input mode), an array for `Tags` and multiple `Select`, Markdown text for long text, and for a `Table` an array of rows shaped `{ "values": { "<column>": <value> } }`. To get plain text from the Markdown, strip it on your side; `MarkdownStripper.Strip` in `Dignite.Vault.Extract.Domain.Shared` does it for .NET.

`GET /api/vault-extract/documents/{id}/blob` downloads the original file. A sub-document has none (`Extract:DocumentNoSourceBlob`); download its parent's.

## Listing and Filtering Documents

`GET /api/vault-extract/documents` returns a page of `DocumentListItemDto`: the document's identity, type, state, title, field values, file metadata, relations and `rights`, but no Markdown, language or review details, plus `deletionTime` for the recycle bin. Query parameters:

| Parameter | Description |
| --- | --- |
| `documentTypeCode` | One type. Required when `fieldFilters` is used |
| `lifecycleStatus` | `10` Uploaded, `20` Processing, `25` PendingReview, `30` Ready, `99` Failed |
| `reviewDisposition` | `0` NotReviewed, `20` Confirmed, `30` Rejected |
| `hasReviewReasons` | `true` lists documents that need attention |
| `cabinetId` | One cabinet |
| `originDocumentId` | The sub-documents of one document |
| `isDeleted` | `true` lists the recycle bin |
| `fieldFilters` | Up to 10 field-value filters, combined with AND |
| `sorting` | `creationTime` (ascending) or `creationTime desc` (the default) |
| `skipCount`, `maxResultCount` | Paging (ABP defaults: 10 per page, at most 1,000) |

Rows are always narrowed to the documents the caller may read. Each field filter names a field and gives either an exact `value` or an inclusive `min` / `max` range; ranges work on number and date fields only. Values are at most 512 characters; dates use `yyyy-MM-dd` or `yyyy-MM-ddTHH:mm:ss` without an offset. In a query string:

```text
GET /api/vault-extract/documents
    ?documentTypeCode=invoice.standard
    &fieldFilters[0].name=invoice_no&fieldFilters[0].value=INV-2026-001
    &fieldFilters[1].name=amount&fieldFilters[1].min=10000
```

An unknown field, or a field that cannot be filtered (long text, table), fails with `Extract:UnknownExtractedField` instead of returning an empty page. See [MCP Server: Search](mcp-server.md#search) for the full rules, which the list and the MCP search share.

To download the same view as a file, see [Data Download](data-download.md).

## Endpoint Reference

### Documents (`/api/vault-extract/documents`)

| Verb | Route | Operation |
| --- | --- | --- |
| `GET` | `/` | List documents |
| `GET` | `/{id}` | Get a document |
| `POST` | `/upload` | Upload (multipart) |
| `GET` | `/{id}/blob` | Download the original file |
| `DELETE` | `/{id}` | Move to the recycle bin |
| `POST` | `/{id}/restore` | Restore from the recycle bin |
| `DELETE` | `/{id}/permanent` | Delete permanently |
| `POST` | `/{id}/confirm-classification` | Confirm a type |
| `POST` | `/{id}/reclassify` | Assign another type |
| `POST` | `/{id}/review/reject` | Reject |
| `POST` | `/{id}/review/allow-duplicate` | Allow a suspected duplicate |
| `POST` | `/{id}/review/resolve-field-validation-warnings` | Dismiss validation warnings |
| `POST` | `/{id}/review/confirm-field-entry` | Confirm manually entered fields |
| `POST` | `/{id}/retry-pipeline` | Retry a failed stage |
| `POST` | `/{id}/reparse` | Re-parse from the original file |
| `POST` | `/{id}/reextract-fields` | Re-extract fields |
| `POST` | `/{id}/extracted-fields` | Replace field values |
| `POST` | `/{id}/markdown` | Correct the Markdown |
| `POST` | `/{id}/cabinet` | Set or clear the cabinet |
| `POST` | `/export` | Download as CSV / XLSX ([Data Download](data-download.md)) |

### Other Resources

| Verb | Route | Operation |
| --- | --- | --- |
| `GET` | `/api/vault-extract/document-statistics` | Counts per status and storage used |
| `GET` | `/api/vault-extract/document-pipeline-runs?documentId=` | A document's [pipeline runs](../pipeline/pipeline-runs.md) |
| `GET` | `/api/vault-extract/document-types` | The layer's active types |
| `GET` | `/api/vault-extract/document-types/deleted` | Deleted types |
| `POST` | `/api/vault-extract/document-types` | Create a type |
| `PUT` | `/api/vault-extract/document-types/{id}` | Update a type |
| `DELETE` | `/api/vault-extract/document-types/{id}` | Delete a type |
| `POST` | `/api/vault-extract/document-types/{id}/restore` | Restore a type |
| `GET` | `/api/vault-extract/document-types/{id}/duplicate-scope-preview?duplicateScope=` | Preview a duplicate-scope change |
| `GET` | `/api/vault-extract/document-type-packs` | Export every type as packs |
| `GET` | `/api/vault-extract/document-type-packs/{id}` | Export one type |
| `POST` | `/api/vault-extract/document-type-packs/import` | Import packs |
| `GET` | `/api/vault-extract/field-definitions?documentTypeId=` | A type's fields (`onlyDeleted=true` for deleted ones) |
| `GET` | `/api/vault-extract/field-definitions/field-types` | Supported field types |
| `POST` | `/api/vault-extract/field-definitions` | Create a field |
| `PUT` | `/api/vault-extract/field-definitions/{id}` | Update a field |
| `DELETE` | `/api/vault-extract/field-definitions/{id}` | Delete a field |
| `POST` | `/api/vault-extract/field-definitions/{id}/restore` | Restore a field |
| `POST` | `/api/vault-extract/field-draft-suggestion/draft` | Draft a field with the LLM |
| `POST` | `/api/vault-extract/field-prompt-polish/polish` | Polish a field instruction with the LLM |
| `POST` | `/api/vault-extract/slug-suggestion/suggest` | Suggest a code from a display name |
| `GET` | `/api/vault-extract/cabinets` | The layer's cabinets |
| `POST` | `/api/vault-extract/cabinets` | Create a cabinet |
| `PUT` | `/api/vault-extract/cabinets/{id}` | Update a cabinet |
| `DELETE` | `/api/vault-extract/cabinets/{id}` | Delete a cabinet |
| `GET` | `/api/vault-extract/document-reprocessing/field-extraction/preview?documentTypeId=` | Preview a batch field re-extraction |
| `POST` | `/api/vault-extract/document-reprocessing/field-extraction` | Start a batch field re-extraction |
| `POST` | `/api/vault-extract/document-reprocessing/reclassification/preview` | Preview a batch reclassification |
| `POST` | `/api/vault-extract/document-reprocessing/reclassification` | Start a batch reclassification |

## Compatibility

Routes and DTO shapes are part of the egress contract. As a `0.y.z` product they may still change between minor versions; every breaking change is marked **BREAKING** in `CHANGELOG.md` with its migration. Adding an optional property is not breaking.

## See Also

- [Documents and Review](../documents/documents.md)
- [MCP Server](mcp-server.md)
- [Integration Events](integration-events.md)
- [Per-Document-Type Permissions](../configuration/document-type-permissions.md)

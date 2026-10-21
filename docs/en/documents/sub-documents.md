```json
//[doc-seo]
{
    "Description": "How Dignite Vault Extract splits a bundle of documents, or a document with embedded standalone documents, into sub-documents that are classified and extracted on their own."
}
```

# Sub-Documents

One uploaded file does not always hold one document. A scan of a whole folder can contain an invoice, a delivery note and a contract; a report can embed a photographed receipt. Vault Extract detects these cases during classification and **splits** the parts into **sub-documents**: ordinary documents, each classified, extracted and published on its own, that point back to the document they came from.

## Two Cases

| Case | Detected when | The parent document | What becomes a sub-document |
| --- | --- | --- | --- |
| **Container** (a bundle) | The classifier judges the document to be a bundle of several documents | Gets **no type and no fields**, and never fires `DocumentReadyEto` | Each constituent document found in its text |
| **Embedded documents** | The classifier flags an embedded standalone document, or the text contains transcribed images (`*[Image OCR]*` blocks) | Keeps its own type and fields | Each embedded standalone document (a photographed receipt inside a report, for example) |

In both cases an LLM reads the whole Markdown once and proposes where each part starts and ends; cover sheets, indexes and transmittal pages are skipped. A container needs at least two parts; anything less, an untrusted split, more than `MaxSegmentsPerDocument` parts (default 50), or Markdown over `MaxSegmentationMarkdownLength` (default 200,000 characters) leaves the container unsplit with the non-blocking `SegmentationIncomplete` review reason. Both limits are in [`Vault:ExtractBehavior`](../configuration/ai-provider.md#cross-cutting-llm-behavior-extractbehavior).

A sub-document is never itself treated as a container, so splitting does not recurse.

## What a Sub-Document Is

- Its **Markdown** is its slice of the parent's Markdown. A transcribed embedded document stays in the parent's Markdown too.
- It has **no file of its own** (`FileOrigin` is null). To show the source, follow `OriginDocumentId` to the parent and use the parent's file; `GET .../documents/{id}/blob` on a sub-document fails with `Extract:DocumentNoSourceBlob`.
- It inherits the parent's **owner** (uploader), but not its cabinet.
- It goes through the normal pipeline: `DocumentUploadedEto` (with `FileName = null`, `FileSize = 0`), text extraction from the slice, classification, field extraction, and its own `DocumentReadyEto` carrying `OriginDocumentId`.
- It does not notify its uploader; the parent does. See [Operator Notifications](../egress/operator-notifications.md).

`DocumentDto` and the document list carry `OriginDocumentId` and `IsContainer`. To list the sub-documents of a document, filter the list by `OriginDocumentId` (REST and the MCP search tool both accept it).

## When the Parent Changes

Sub-documents always derive from the parent's **current** text and classification:

| Change to the parent | Effect on its sub-documents |
| --- | --- |
| Re-parsed from its file | Every sub-document is withdrawn (moved to the recycle bin with `DocumentDeletedEto`) and the new text is split from scratch |
| A container is assigned a concrete type by an operator | The container's parts are withdrawn; embedded documents are kept, because they stand on their own |
| A typed document is found to be a container on re-parse | `DocumentReclassifiedToContainerEto` tells consumers to retract what they derived from the old type, and the document is split |

A withdrawn sub-document cannot be restored (`Extract:DocumentRestoreSuperseded`): its parent's current split no longer produces it.

## Restrictions

| Operation | Rule | Error |
| --- | --- | --- |
| Re-parse a sub-document | Not possible; re-parse the parent | `Extract:DocumentReparseSubDocument` |
| Correct a container's Markdown | Not possible; its parts are derived from it | `Extract:CannotCorrectContainerMarkdown` |

### Deleting and Restoring

| Operation | Rule | Error |
| --- | --- | --- |
| Delete a parent | Refused while any sub-document is live | `Extract:DocumentHasSubDocuments` |
| Permanently delete a parent | Refused while any sub-document exists, recycle bin included, because it would delete the only file they can point to | `Extract:DocumentHasSubDocumentsPermanentDelete` |
| Restore a sub-document | Refused when a live sibling already covers the same part, or the part is no longer in the parent's split | `Extract:DocumentRestoreConflict`, `Extract:DocumentRestoreSuperseded` |

Delete the sub-documents first, then the parent. Neither operation cascades.

## Who Can See Them

A container has no type, so like any untyped document it is visible only to users with `VaultExtract.Documents.ReadAll` and to its uploader. Sub-documents, once classified, follow their own type's permissions. See [Per-Document-Type Permissions](../configuration/document-type-permissions.md).

## See Also

- [Documents and Review](documents.md)
- [Classification](../pipeline/classification.md)
- [Integration Events](../egress/integration-events.md)
- [Reprocessing](../pipeline/reprocessing.md)

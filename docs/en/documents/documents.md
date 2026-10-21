```json
//[doc-seo]
{
    "Description": "How documents move through Dignite Vault Extract: uploading, lifecycle statuses, review reasons and the Ready gate, operator review actions, corrections and the recycle bin."
}
```

# Documents and Review

A **document** is one uploaded file and everything the channel derives from it: the Markdown, the title and language, the classified type, and the extracted field values. This page follows a document from upload to Ready, and describes what an operator does when the pipeline cannot decide on its own.

## The Pipeline

```text
upload ──► text extraction ──► classification ──► field extraction ──► Ready
              (Markdown)        (type, or        (field values,       (DocumentReadyEto)
                                 container)       duplicate check)
```

Each stage runs as a background job and leaves a [pipeline run](../pipeline/pipeline-runs.md) record. Text extraction, classification and field extraction are the **key pipelines**: their latest runs, together with the document's review reasons, decide its lifecycle status. A container document (a bundle of several documents) is split into [sub-documents](sub-documents.md) instead of having its fields extracted.

## Uploading

Upload from **Vault Extract → Document List**, or call `POST /api/vault-extract/documents/upload` with a `multipart/form-data` body:

| Field | Required | Description |
| --- | --- | --- |
| `File` | Yes | The file |
| `CabinetId` | No | File the document into a [cabinet](cabinets.md). Needs `VaultExtract.Cabinets` |
| `DocumentTypeId` | No | Declare the type and skip LLM classification. See [Declared type at upload](../pipeline/classification.md#declared-type-at-upload) |

### Accepted Files

A file is accepted only when its extension **and** content type form one of these pairs:

| Extension | Content types |
| --- | --- |
| `.pdf` | `application/pdf` |
| `.jpg`, `.jpeg` | `image/jpeg` |
| `.png` | `image/png` |
| `.gif` | `image/gif` |
| `.webp` | `image/webp` |
| `.docx` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` |
| `.xlsx` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` |
| `.pptx` | `application/vnd.openxmlformats-officedocument.presentationml.presentation` (needs the OpenXml provider; see [Text Extraction](../text-extraction/text-extraction.md)) |
| `.csv` | `text/csv`, `application/csv`, `application/vnd.ms-excel` |
| `.tsv` | `text/tab-separated-values`, `text/tsv` |
| `.txt` | `text/plain` |

`application/octet-stream` and `application/zip` are never accepted. Anything else fails with `Extract:DocumentUnsupportedFileType`.

The size limit is **20 MiB** (`DocumentConsts.MaxUploadFileBytes`); a larger file fails with `Extract:DocumentFileTooLarge`. The limit is enforced on the bytes actually received, not only on the declared length. It is a static property: a host may change it at startup, and must then raise the server's request limits as well (the shipped host derives Kestrel's and the form's limits from it). The allowed-type table is likewise the static `DocumentConsts.AllowedUploadContentTypesByExtension`.

### Checks at Upload

In order, the upload is refused when:

1. The declared type does not exist in the caller's layer (`404`), or the caller may not upload into it. See [Per-Document-Type Permissions](../configuration/document-type-permissions.md#upload).
2. The caller's layer has no document types at all (`Extract:NoDocumentTypesConfigured`): there would be nothing to classify into.
3. The cabinet does not exist (`Extract:InvalidCabinetId`).
4. The file type or size is not allowed.
5. **The same user already uploaded the same bytes** (SHA-256 of the content): `Extract:DocumentDuplicate` with the existing document's id, or `Extract:DocumentInRecycleBin` when that document is in the recycle bin. This check is per uploader; detecting the same business document uploaded by someone else, or scanned twice, is [duplicate detection](document-types.md#duplicate-detection).

A successful upload stores the original file in BLOB storage, creates the document (status `Uploaded`), publishes `DocumentUploadedEto`, and queues text extraction.

## Lifecycle Statuses

| Status | Value | Meaning |
| --- | --- | --- |
| `Uploaded` | 10 | Stored; no pipeline has started |
| `Processing` | 20 | A key pipeline is queued or running |
| `PendingReview` | 25 | The pipelines have finished, but a **blocking** review reason holds the document back |
| `Ready` | 30 | Every key pipeline succeeded and no blocking reason is left; `DocumentReadyEto` is published |
| `Failed` | 99 | A key pipeline failed, or an operator rejected the document |

The status is derived, never set by hand: after every pipeline step and every operator action, the latest key-pipeline runs and the review reasons are evaluated again (a failed run wins, then a blocking reason, then all-succeeded). So an operator action that removes the last blocking reason releases the document to Ready, and an edit that introduces one sends it back to review.

## Review Reasons

A document carries a set of **review reasons**, each raised and cleared by one stage. **Blocking** reasons keep the document out of Ready; the others are informational.

| Reason | Blocking | Raised when | Cleared by |
| --- | --- | --- | --- |
| `UnresolvedClassification` | Yes | Classification confidence is below the type's threshold, no type fits, or the LLM output was unusable | A confident re-classification; an operator confirming or assigning a type |
| `DuplicateSuspected` | Yes | Another document of the type has the same [unique-key values](document-types.md#duplicate-detection) | Allowing the duplicate; a type change; the type losing its unique keys |
| `FieldExtractionIncomplete` | Yes | Field extraction was declined because the Markdown exceeds `MaxFieldExtractionMarkdownLength` | An operator confirming the field entry; a later successful extraction (for example after a type change) |
| `FieldValidationWarning` | Yes | An extracted value broke a rule stated in its field's instruction ([Validation Warnings](document-types.md#validation-warnings)) | An operator dismissing the warnings; a clean re-extraction; a type change |
| `MissingRequiredFields` | No | A required field has no value | The value being supplied, by extraction or by an operator |
| `SegmentationIncomplete` | No | A container could not be split reliably (see [Sub-Documents](sub-documents.md)) | A later successful split; a type change |

Independently of the reasons, each document has a **review disposition**: `NotReviewed` (the default), `Confirmed` (an operator confirmed or assigned the type, or the type was declared at upload) or `Rejected`. A document **needs attention** while it has any reason and is not rejected; that is the **Needs review** filter of the document list.

The document's detail view (`DocumentDto.ReviewReasonDetails`) explains each reason: the missing required fields, the suspected duplicates (limited to documents the caller may read), and the validation warnings.

## Reviewing a Document

The detail page offers the actions the current user may take; the server decides them and sends them with the document (`DocumentDto.Rights`). Each action is also a REST endpoint under `/api/vault-extract/documents/{id}`.

| Action | Endpoint | Effect |
| --- | --- | --- |
| Confirm or change the type | `POST confirm-classification`, `POST reclassify` | Sets the chosen type as operator-confirmed (confidence `1.0`) and re-extracts its fields. No LLM classification. Needs the document's text. Works in any state, including after a rejection |
| Reject | `POST review/reject` | Records a mandatory reason (up to 2,048 characters), sets the disposition to `Rejected` and the status to `Failed`. The original file, Markdown and reason are kept for audit |
| Allow a suspected duplicate | `POST review/allow-duplicate` | Clears `DuplicateSuspected`; the decision holds until the document's key values change |
| Dismiss validation warnings | `POST review/resolve-field-validation-warnings` | Removes the listed warnings (up to 32 per call); the reason clears when none remain |
| Confirm the field entry | `POST review/confirm-field-entry` | Clears `FieldExtractionIncomplete` after the operator has entered the values by hand |
| Retry a failed stage | `POST retry-pipeline` | Queues a new run of `text-extraction`, `classification` or `field-extraction` when its latest run failed |

Rejecting, allowing a duplicate, dismissing warnings and confirming the field entry are **review** acts: the uploader cannot perform them on their own document through ownership alone. See [Per-Document-Type Permissions](../configuration/document-type-permissions.md).

## Correcting a Document

| Action | Endpoint | Effect |
| --- | --- | --- |
| Edit field values | `POST extracted-fields` | Replaces the whole value set after validating each value against its field type. Re-evaluates required fields and the duplicate key; does not clear validation warnings. A document that stays Ready re-publishes `DocumentReadyEto` |
| Correct the Markdown | `POST markdown` | Overwrites the Markdown only (the title, language and fields stay). With `Reprocess` set, also re-extracts the fields from the corrected text. Not available on containers |
| Re-extract fields | `POST reextract-fields` | Runs field extraction again on the current Markdown and type |
| Re-parse | `POST reparse` | Extracts the text again from the original file and re-runs the whole pipeline. See [Reprocessing](../pipeline/reprocessing.md#single-document-re-parse) |
| File into a cabinet | `POST cabinet` | Sets or clears the document's [cabinet](cabinets.md) |

An action that starts a pipeline stage is refused while that stage is already queued or running, and every action is refused on a document in the recycle bin.

## Deleting and Restoring

**Deleting** moves a document to the recycle bin (**Vault Extract → Recycle Bin**) and publishes `DocumentDeletedEto`. **Restoring** brings it back and publishes `DocumentRestoredEto`; a document whose type has since been deleted cannot be restored (`Extract:DocumentRestoreTypeDeleted`). **Permanently deleting** removes the document and its original file and publishes `DocumentPermanentlyDeletedEto`; it works on live and recycled documents alike and needs `VaultExtract.Documents.PermanentDelete`.

A document with sub-documents cannot be deleted while any of them is live, nor permanently deleted while any of them exists, because a sub-document reaches its source file only through its parent. See [Sub-Documents](sub-documents.md#deleting-and-restoring).

## Statistics

The **Overview** page shows the layer's document counts per status, the number that need review, and the storage used by original files (`GET /api/vault-extract/document-statistics`). The counts and the storage figure exclude the recycle bin and container documents; sub-documents add no storage, since they have no file of their own. Statistics need `VaultExtract.Documents.ReadAll`.

## See Also

- [Document Types and Fields](document-types.md)
- [Classification](../pipeline/classification.md)
- [Reprocessing](../pipeline/reprocessing.md)
- [Per-Document-Type Permissions](../configuration/document-type-permissions.md)
- [Integration Events](../egress/integration-events.md)
- [REST API](../egress/rest-api.md)

```json
//[doc-seo]
{
    "Description": "How Dignite Vault Extract classifies documents into the types of their own layer: the LLM workflow, confidence thresholds, outcomes, operator correction and declared types."
}
```

# Document Classification

When a document finishes [text extraction](../text-extraction/text-extraction.md), Dignite Vault Extract classifies it against `DocumentType` rows that belong to the same layer as the document (Host documents → `TenantId IS NULL` rows; tenant documents → matching tenant rows). The Host deployer creates their types through the admin UI (`IDocumentTypeAppService`); tenants do the same for their own private types. Dignite Vault Extract ships **no built-in types** and **does not register types in Module startup** — every type is owned by the deployer or tenant, never by Dignite Vault Extract itself.

The resulting type decides the next stage — field extraction for that type's fields, in the document's own layer — and is broadcast as `DocumentClassifiedEto` (carrying `DocumentTypeCode`) so downstream consumers can follow along. Classification also decides whether the document is a **container** of several documents, or embeds standalone documents, which are then split into [sub-documents](../documents/sub-documents.md).

This page covers the classification pipeline as a *feature*: how it works, how to tune it, and what happens when the LLM is unhappy. For low-level orchestration code see `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/Classification/`.

## How It Works

```
Document.Markdown ──► DocumentClassificationBackgroundJob ──► DocumentClassificationWorkflow
                                                              (ChatClientAgent + structured output)
                                                                         │
                                                                         ▼
                                            ConfidenceScore ≥ Type.ConfidenceThreshold ?
                                                ├─ yes ─► DocumentClassifiedEto + field extraction scheduled
                                                └─ no  ─► review queue (ReviewReasons += UnresolvedClassification)

                              transient LLM error          ──► rethrow → ABP Job retry (MaxTryCount)
                              schema deserialization error ──► review queue (no retry)
```

Two design properties matter:

- **The LLM consumes Markdown directly.** For structured documents (contracts, reports, layout-aware OCR output), headings, tables and lists in `Document.Markdown` are kept as **real semantic signals** the LLM exploits. The system prompt explicitly tells the model "input is Markdown". For unstructured content (loose OCR paragraphs, plain text), the Markdown wrapper is a container name — it keeps the classifier on one prompt template, but no extra signal is being conveyed beyond what plain paragraphs would carry.
- **Transient LLM failures rely on ABP Job retry, not a keyword fallback.** Network errors, timeouts and cancellations bubble out of `DocumentClassificationBackgroundJob`; the `PipelineRun` is marked `Failed` for operator visibility, and ABP reschedules the job per `BackgroundJobOptions.JobTypes` retry policy. When the LLM recovers, the next attempt produces a real classification — far better than freezing a document on a low-confidence keyword guess. Schema deserialization errors short-circuit straight to the review queue (`ReviewReasons` gets `UnresolvedClassification`) because retrying the same malformed output wastes quota.

## Registering Document Types

Both Host deployers and tenants create their `DocumentType` rows through the admin UI (`IDocumentTypeAppService`), each in their own layer. There is **no module-startup registration path** — Dignite Vault Extract ships with no built-in types, and there is no inheritance: a Host type never applies to tenant documents. The full list of type properties is in [Document Types and Fields](../documents/document-types.md#document-types); these are the ones classification reads:

| Field | Used by |
|---|---|
| `TypeCode` | Carried as `DocumentTypeCode` on every egress; downstream consumers route on `(TenantId, DocumentTypeCode)`. Documents and fields reference the type by id, so the code can be renamed. Dot-separated segments, for example `invoice.standard` |
| `DisplayName` | Sent to the LLM as the candidate name. A plain string, not a localization key, since each layer edits its own rows |
| `Description` | Sent to the LLM with the name; describe what sets this type apart |
| `Priority` | Higher = appears earlier in the LLM prompt; tie-break when truncated to `MaxDocumentTypesInClassificationPrompt`. |
| `ConfidenceThreshold` | Default `0.7`. The LLM result must reach it to be accepted; below it the document enters the review queue (`ReviewReasons` gets `UnresolvedClassification`, `ReviewDisposition` stays `NotReviewed`) |

## Configuration

```json
"Vault": {
  "ExtractBehavior": {
    "MaxDocumentTypesInClassificationPrompt": 50,
    "MaxTextLengthPerExtraction": 8000
  }
}
```

| Key | Default | Description |
| --- | --- | --- |
| `MaxDocumentTypesInClassificationPrompt` | `50` | When more than this many types are registered, the prompt keeps the top N by `Priority`. Tune this against your LLM's context window — more types means a longer prompt and slower / more expensive calls. |
| `MaxTextLengthPerExtraction` | `8000` | Markdown longer than this is truncated before being sent. The first N characters usually contain the most discriminative content (title, table-of-contents, opening clauses). Increase if your documents bury the type signal deep, but watch token cost. |

The prompt language follows `Vault:ExtractBehavior:DefaultLanguage` (see [ai-provider.md](../configuration/ai-provider.md#cross-cutting-llm-behavior-extractbehavior)).

## Outcomes

| Outcome | Pipeline state | What happens next |
|---|---|---|
| LLM result, confidence ≥ threshold | `DocumentPipelineRun` completes | `DocumentClassifiedEto` published; field extraction for the type is scheduled in the same transaction |
| LLM judges the document a container | Run completes; no type, no fields | The document is split into [sub-documents](../documents/sub-documents.md); it never fires `DocumentReadyEto`, its sub-documents do |
| LLM result, confidence < threshold | review queue (`ReviewReasons` = `UnresolvedClassification`; `DocumentTypeId` cleared, lifecycle stays `Processing`) | `PipelineRunExtraPropertyNames.ClassificationCandidates` is populated for the UI ([pipeline-runs.md](pipeline-runs.md)) |
| No suitable `DocumentType` / `DocumentTypeCode == null` | review queue (`UnresolvedClassification`) | The operator uses the `ClassificationCandidates` payload ([pipeline-runs.md](pipeline-runs.md)) to create a matching `DocumentType`, then assigns it (`ReclassifyAsync`), rejects the document (`RejectReviewAsync`, reason required), or re-uploads a better source document |
| LLM unreachable (transient) | `Failed`, exception rethrown | ABP retries the job per `BackgroundJobOptions.JobTypes` `MaxTryCount`. Next attempt does a fresh LLM classification once the provider recovers. |
| LLM returned malformed JSON | review queue (`UnresolvedClassification`) | No retry — a human resolves the type code in the UI |

Operator confirmation/correction (`ConfirmClassificationAsync` / `ReclassifyAsync`) requires text extraction to have already completed — the same `Document.Markdown` precondition as `ReparseAsync` — and throws `NotTextExtracted` otherwise, so a type can never be confirmed on a document that has no text yet to run the field-extraction cascade against.

On the detail page both are one control: the type editor beside the type badge ([#660](https://github.com/dignite-projects/vault-extract/issues/660)), which the review banner's *Confirm classification* button also opens. Saving writes the chosen type as operator-confirmed and re-extracts fields for it — no re-parse and no LLM classification. To have the LLM classify a document again, re-parse it ([reprocessing.md](reprocessing.md)); there is no longer a single-document action that re-classifies the existing text without re-parsing it.

## Declared Type at Upload

`UploadDocumentInput.DocumentTypeId` (#623) lets a caller who already knows the type skip this whole stage. Supplying it is equivalent to an operator calling `ConfirmClassificationAsync` on the document: `DocumentTypeId` is set, `ClassificationConfidence` is pinned to `1.0`, `ReviewDisposition` becomes `Confirmed`, and **no classification LLM call is made** — the document never enters the review queue for `UnresolvedClassification`. This is aimed at business-system integrations that already know what they are submitting (an invoice-scanning station, an HR system uploading a known contract) and at MCP ingest callers that were told the type.

Mechanically, the declaration is stamped onto the `Document` at upload time, but the `Classification` pipeline stage itself is still completed later, right after [text extraction](../text-extraction/text-extraction.md) finishes — `DocumentParseBackgroundJob`'s cascade branch sees the declared type and completes `Classification` as a manual classification (same run bookkeeping, same transactional field-extraction scheduling, same `DocumentClassifiedEto` shape as an operator confirmation) instead of enqueuing the LLM job. `DocumentClassifiedEto` therefore still fires in the correct stage order, after `DocumentTextExtractedEto` — no ETO contract change.

A few consequences worth knowing:

- **Container detection and embedded-document routing are skipped.** Both ride the classification stage; a document with a declared type is treated as a concrete document, exactly like an operator `Reclassify` to a concrete type today. If the caller declared a type for what is actually a bundle of several documents, the operator's remedy is the same as today: re-parse ([reprocessing.md](reprocessing.md)), which always runs the LLM and can detect the container.
- **This only short-circuits the first automatic classification.** An operator re-parse (`ReparseAsync`) afterward always runs the real LLM classification, the same as re-parsing an operator-confirmed document.
- **Declaring a type is an upload into that type.** `UploadAsync` is judged once, against the declared type: either the module-wide `Documents.Upload`, which covers every type of the caller's layer, or an ABP resource-permission grant of `Upload` on that one type, which is enough on its own. The id must resolve to a `DocumentType` in the caller's own layer (Host or the caller's tenant); an unknown or cross-layer id fails the upload with `EntityNotFoundException` **before** any permission is consulted, so a grant on a Host-layer type can never authorize a tenant caller. See [per-document-type permissions](../configuration/document-type-permissions.md).
- **Leaving `DocumentTypeId` null requires the module-wide `Documents.Upload`.** Untyped upload hands the type decision to the LLM, which may land the document in any type of the layer, and it then reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)` — so a caller whose upload right is a per-type grant must name the type. This is what `Documents.Upload` meant in every released version.
- **An unclassified document is invisible to a caller without `Documents.ReadAll`, except its uploader.** Until the classification stage assigns a type, the document belongs to no type, and no per-type grant can name it — so it is excluded from the document list, the export and the MCP search of every other caller narrowed to per-type `Read` grants, and `GetAsync` on it is denied to them. The same applies to a document whose classification failed and to a container. Its uploader still reaches it through ownership (#635). Reviewing the unresolved-classification queue as a whole therefore requires `ReadAll`. See [per-document-type permissions](../configuration/document-type-permissions.md).

## See Also

- [Document Types and Fields](../documents/document-types.md) — defining the types classified into
- [Documents and Review](../documents/documents.md) — the review queue and the operator actions
- [Text extraction](../text-extraction/text-extraction.md) — produces the `Document.Markdown` consumed here
- [Pipeline runs](pipeline-runs.md) — the `Candidates` payload schema for the review UI
- [Reprocessing](reprocessing.md) — re-running classification over existing documents in bulk after you change a type's prompt / threshold or add a new type

# Document Classification

When a document finishes [text extraction](../text-extraction/text-extraction.md), Dignite Vault Extract classifies it against `DocumentType` rows that belong to the same layer as the document (Host documents → `TenantId IS NULL` rows; tenant documents → matching tenant rows). The Host deployer creates their types through the admin UI (`IDocumentTypeAppService`); tenants do the same for their own private types. Dignite Vault Extract ships **no built-in types** and **does not register types in Module startup** — every type is owned by the deployer or tenant, never by Dignite Vault Extract itself.

The resulting `DocumentTypeCode` is the routing signal that drives the next channel stages — Host field extraction (#168) for type-bound Host fields and tenant field extraction (#169) for tenant-defined fields — and is also broadcast via `DocumentClassifiedEto` over `DistributedEventBus` so downstream business consumers (in their own repositories) can subscribe and persist their own derived records.

This page covers the classification pipeline as a *feature*: how it works, how to tune it, and what happens when the LLM is unhappy. For low-level orchestration code see `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/Classification/`.

## How it works

```
Document.Markdown ──► DocumentClassificationBackgroundJob ──► DocumentClassificationWorkflow
                                                              (ChatClientAgent + structured output)
                                                                         │
                                                                         ▼
                                            ConfidenceScore ≥ Type.ConfidenceThreshold ?
                                                ├─ yes ─► DocumentClassifiedEto + enqueue Host / tenant field extraction
                                                └─ no  ─► review queue (ReviewReasons += UnresolvedClassification)

                              transient LLM error          ──► rethrow → ABP Job retry (MaxTryCount)
                              schema deserialization error ──► review queue (no retry)
```

Two design properties matter:

- **The LLM consumes Markdown directly.** For structured documents (contracts, reports, layout-aware OCR output), headings, tables and lists in `Document.Markdown` are kept as **real semantic signals** the LLM exploits. The system prompt explicitly tells the model "input is Markdown". For unstructured content (loose OCR paragraphs, plain text), the Markdown wrapper is a container name — it keeps the classifier on one prompt template, but no extra signal is being conveyed beyond what plain paragraphs would carry.
- **Transient LLM failures rely on ABP Job retry, not a keyword fallback.** Network errors, timeouts and cancellations bubble out of `DocumentClassificationBackgroundJob`; the `PipelineRun` is marked `Failed` for operator visibility, and ABP reschedules the job per `BackgroundJobOptions.JobTypes` retry policy. When the LLM recovers, the next attempt produces a real classification — far better than freezing a document on a low-confidence keyword guess. Schema deserialization errors short-circuit straight to the review queue (`ReviewReasons` gets `UnresolvedClassification`) because retrying the same malformed output wastes quota.

## Registering document types

Both Host deployers and tenants create their `DocumentType` rows through the admin UI (`IDocumentTypeAppService`), each in their own layer. There is **no Module-startup registration path** — Dignite Vault Extract Core ships with no built-in types, and there's no inheritance: a Host type never auto-applies to tenant documents.

| Field | Used by |
|---|---|
| `TypeCode` | Downstream consumers (DistributedEventBus subscribers) match on this code; `FieldDefinition` rows also key on it. Convention: `<owner>.<sub-type>` (e.g. `host.general`, `tenant-acme.case-file`). |
| `DisplayName` (`string`) | Sent to the LLM as the candidate name. Stored as a plain string on the entity — the admin UI presents it directly without any `IStringLocalizerFactory` lookup, since each tenant edits their own row. |
| `Priority` | Higher = appears earlier in the LLM prompt; tie-break when truncated to `MaxDocumentTypesInClassificationPrompt`. |
| `ConfidenceThreshold` | LLM result must clear this to auto-classify; below it the document enters the review queue (`ReviewReasons` gets `UnresolvedClassification`, `ReviewDisposition` stays `NotReviewed`). |

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
| LLM result, confidence ≥ threshold | `DocumentPipelineRun` completes | `DocumentClassifiedEto` published; Host & tenant field extraction enqueued; downstream `DistributedEventBus` subscribers (in their own repos) receive the event |
| LLM result, confidence < threshold | review queue (`ReviewReasons` = `UnresolvedClassification`; `DocumentTypeId` cleared, lifecycle stays `Processing`) | `PipelineRunExtraPropertyNames.ClassificationCandidates` is populated for the UI ([pipeline-runs.md](pipeline-runs.md)) |
| No suitable `DocumentType` / `DocumentTypeCode == null` | review queue (`UnresolvedClassification`) | The operator uses the `ClassificationCandidates` payload ([pipeline-runs.md](pipeline-runs.md)) to create a matching `DocumentType`, then reclassifies (`ReclassifyAsync`), rejects (`RejectReviewAsync`, reason required), or re-uploads a better source document |
| LLM unreachable (transient) | `Failed`, exception rethrown | ABP retries the job per `BackgroundJobOptions.JobTypes` `MaxTryCount`. Next attempt does a fresh LLM classification once the provider recovers. |
| LLM returned malformed JSON | review queue (`UnresolvedClassification`) | No retry — a human resolves the type code in the UI |

Operator confirmation/correction (`ConfirmClassificationAsync` / `ReclassifyAsync`) requires text extraction to have already completed — the same `Document.Markdown` precondition as `RerecognizeAsync` — and throws `NotTextExtracted` otherwise, so a type can never be confirmed on a document that has no text yet to run the field-extraction cascade against.

## Declared type at upload

`UploadDocumentInput.DocumentTypeId` (#623) lets a caller who already knows the type skip this whole stage. Supplying it is equivalent to an operator calling `ConfirmClassificationAsync` on the document: `DocumentTypeId` is set, `ClassificationConfidence` is pinned to `1.0`, `ReviewDisposition` becomes `Confirmed`, and **no classification LLM call is made** — the document never enters the review queue for `UnresolvedClassification`. This is aimed at business-system integrations that already know what they are submitting (an invoice-scanning station, an HR system uploading a known contract) and at MCP ingest callers that were told the type.

Mechanically, the declaration is stamped onto the `Document` at upload time, but the `Classification` pipeline stage itself is still completed later, right after [text extraction](../text-extraction/text-extraction.md) finishes — `DocumentParseBackgroundJob`'s cascade branch sees the declared type and completes `Classification` as a manual classification (same run bookkeeping, same transactional field-extraction scheduling, same `DocumentClassifiedEto` shape as an operator confirmation) instead of enqueuing the LLM job. `DocumentClassifiedEto` therefore still fires in the correct stage order, after `OCRCompletedEto` — no ETO contract change.

A few consequences worth knowing:

- **Container detection and embedded-document routing are skipped.** Both ride the classification stage; a document with a declared type is treated as a concrete document, exactly like an operator `Reclassify` to a concrete type today. If the caller declared a type for what is actually a bundle of several documents, the operator's remedy is the same as today: re-recognize, which runs the LLM and can detect the container.
- **This only short-circuits the first automatic classification.** An operator "re-recognize" (`RerecognizeAsync`) afterward always runs the real LLM classification, the same as re-recognizing an operator-confirmed document.
- **Declaring a type requires an additional permission, per type.** Because it bypasses the review queue, `UploadAsync` requires more than the method-level `Documents.Upload` when `DocumentTypeId` is supplied — the same additive-permission shape as the `CabinetId` / `Cabinets.Default` check. Either of two things admits the caller: the module-wide `Documents.ConfirmClassification`, which covers every type of the caller's layer, or an ABP resource-permission grant of `Upload` on that one type. The id must resolve to a `DocumentType` in the caller's own layer (Host or the caller's tenant); an unknown or cross-layer id fails the upload with `EntityNotFoundException` **before** any permission is consulted, so a grant on a Host-layer type can never authorize a tenant caller. See [per-document-type upload permissions](../configuration/document-type-upload-permissions.md).
- **Leaving `DocumentTypeId` null also requires `Documents.ConfirmClassification`.** Untyped upload hands the type decision to the LLM, which would otherwise be a way around the per-type grant — upload untyped, let classification land the document in a restricted type, and it still reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)`. This is a behaviour change; a role that holds `Upload` without `ConfirmClassification` and relies on untyped upload has to be granted `ConfirmClassification` (see the changelog entry for the release that introduces it).

## See also

- [Text extraction](../text-extraction/text-extraction.md) — produces the `Document.Markdown` consumed here
- [Pipeline runs](pipeline-runs.md) — the `Candidates` payload schema for the review UI
- [Reprocessing](reprocessing.md) — re-running classification over existing documents in bulk after you change a type's prompt / threshold or add a new type

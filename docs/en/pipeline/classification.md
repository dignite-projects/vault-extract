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

## See also

- [Text extraction](../text-extraction/text-extraction.md) — produces the `Document.Markdown` consumed here
- [Pipeline runs](pipeline-runs.md) — the `Candidates` payload schema for the review UI
- [Reprocessing](reprocessing.md) — re-running classification over existing documents in bulk after you change a type's prompt / threshold or add a new type

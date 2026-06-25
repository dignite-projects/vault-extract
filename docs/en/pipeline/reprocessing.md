# Document Reprocessing

When you change a **classification prompt** (a `DocumentType`'s `Description`, `ConfidenceThreshold`, or `Priority`, or add/remove a type) or a **field definition**, documents that were already processed keep their *old* results. Reprocessing re-runs the relevant pipeline over existing documents so they pick up the new configuration.

The forward pipeline is `upload → text-extraction → classification → field-extraction → Ready`. Reprocessing re-runs from the **classification** or **field-extraction** stage — never text extraction (re-OCR of existing documents is out of scope, see below).

**Judgment stays with the operator.** Config changes do **not** cascade automatically — Dignite Vault Extract never guesses whether you meant a change to apply to existing documents. You trigger reprocessing explicitly, choose its scope, and accept its cost. This page covers the feature; for orchestration code see `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/Reprocessing/` and `.../FieldExtraction/`.

## Three entry points

| Operation | Trigger when you changed | Cascade | Destructive? | Warning |
|---|---|---|---|---|
| **Batch field re-extraction** | field definitions | none (leaf) | overwrites field values only | light |
| **Batch reclassification** | classification prompt / thresholds / add/remove types | re-extracts fields too | overwrites classification, clears fields on low confidence | heavy |
| **Single-document "Re-extract fields"** | a field definition, for one document | none (leaf) | overwrites that document's field values | per-document confirm |

Reclassification **⊇** field re-extraction: a successful reclassification re-publishes `DocumentClassifiedEto`, which cascades into field re-extraction anyway. Pick by what you changed — if you only touched field definitions, use field re-extraction (cheaper, no classification side effects); if you touched the classification prompt, use reclassification (it refreshes fields as well).

## Batch field re-extraction

Re-runs **only** type-bound field extraction (the `field-extraction` pipeline) over existing documents of one type, on their existing Markdown and classification. It does **not** re-OCR or re-classify.

- **Scope**: all text-extracted documents whose `DocumentTypeId` is the selected type (current layer only; recycle-bin documents excluded).
- **Overwrites**: each document's extracted field values are replaced as a group — including any values an operator corrected manually. This is the "light warning" the preview shows.
- **Lifecycle-neutral**: `field-extraction` is deliberately **not** a key pipeline (`ExtractPipelines.KeyPipelines` = `{ text-extraction, classification }`), so re-extracting fields never flips an already-`Ready` document back to `Processing`. The `DocumentPipelineRun` row exists for observability and retry only.
- **Entry points**: the document-type list (per-type action) and the field-definition page header; the preview shows the affected document count and the type's current field names.

## Batch reclassification

Re-runs the **classification** pipeline. Because classification is a key pipeline, a successful run re-publishes `DocumentClassifiedEto` and cascades into field re-extraction; a low-confidence result sends the document to the review queue (`ReviewReasons += UnresolvedClassification`) and clears its type-bound fields. This is why it carries a **heavy warning**.

### Scope — chosen by the operator, no default

Classification is a "candidate-set competition": changing one type's description changes the whole classification function, so any document's verdict can change. The scope is yours to pick by intent:

| Scope | Reaches | Cost / blast radius |
|---|---|---|
| **Only the current type** | documents currently classified as this type | cheapest, localized; **blind spot**: cannot pull in documents that *should* belong here but were classified elsewhere |
| **All documents (cross-type)** | every text-extracted document in the layer | the **only** scope that both evicts and pulls in — required to make a **new type** take effect or to gather scattered same-kind documents; largest blast radius |
| **Pending-review queue** | documents that previously failed to classify (`ReviewReasons` has `UnresolvedClassification`) | small, safe — re-judges documents that never got a confident type |

> To make a newly created type take effect, you **must** use *All documents* — a new type has no documents "already classified as it" for the narrow scope to evict from.

### Protecting manual confirmation (on by default)

A manually confirmed type (`ReviewDisposition = Confirmed`) is a higher-priority signal than automatic classification. By default reclassification **skips** confirmed documents. The preview offers an explicit *"Also reclassify documents confirmed by an operator"* toggle — turning the override of operator work into a deliberate opt-in. (The pending-review-queue scope ignores the toggle: those documents are unconfirmed by definition.)

## Single-document "Re-extract fields"

On the document detail page, next to **Re-recognize**, a lighter **Re-extract fields** button re-runs only field extraction for that one document on its existing classification — no reclassification, no OCR. Use it when you tweaked a field definition and want to refresh one document without touching its type.

It differs from **Re-recognize** (`RerecognizeAsync`, [#263](https://github.com/dignite-projects/vault-extract/issues/263)), which re-runs *classification* and cascades — a destructive operation. Re-extract fields is the safe leaf version. It is rejected if the document is in the recycle bin, not yet classified, has no Markdown, or already has a field-extraction run in progress.

## How batches run (mechanism)

Triggering a batch enqueues a single **dispatcher** background job and returns immediately with an estimated document count. The dispatcher then, per batch:

1. reads the next page of document **Ids only** via keyset pagination (`WHERE Id > lastId ORDER BY Id Take(N)`, `AsNoTracking().Select(Id)` — never loads Markdown), in a short unit of work;
2. enqueues one single-document job per Id;
3. if the page was full, enqueues the **next dispatcher** with the cursor (chained self-continuation) and exits.

Every dispatcher is a few-seconds short task, so there is no long-running job holding a worker. Each single-document job is idempotent (`SetFields` / classification results are whole-group replacements), so the at-least-once delivery semantics of the background-job system — including the rare duplicate after a crash — are harmless: re-running produces the same end state, at worst extra LLM cost. There is **no dedicated batch/progress state table**; continuation, cursor, and scope all live in the job arguments and the documents' own state.

### Throughput

Single-document jobs run on the host's background-job manager. The default host ships ABP's built-in manager, which processes jobs **serially** — correct for any batch size, but large batches finish slowly. Concurrency is a host **deployment** concern: a deployer who needs parallel workers can configure a concurrent background-job provider in their own host. Whatever the provider, keep the worker count matched to your LLM provider's rate limits — every job makes an external LLM call.

## Permissions

| Permission | Gates |
|---|---|
| `Extract.Documents.Reprocessing.FieldExtraction` | batch field re-extraction (preview + trigger) |
| `Extract.Documents.Reprocessing.Reclassification` | batch reclassification (preview + trigger) |
| `Extract.Documents.ConfirmClassification` | single-document *Re-extract fields* (operator-level, same as *Re-recognize*) |

## REST endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/extract/document-reprocessing/field-extraction/preview?documentTypeId=` | affected count + the type's current field names |
| `POST` | `/api/extract/document-reprocessing/field-extraction` | start batch field re-extraction |
| `POST` | `/api/extract/document-reprocessing/reclassification/preview` | affected count for a scope |
| `POST` | `/api/extract/document-reprocessing/reclassification` | start batch reclassification |
| `POST` | `/api/extract/documents/{id}/reextract-fields` | single-document re-extract fields |

Batch and progress are internal operational state — they are **not** part of the exit contract. Downstream consumers see the normal staged events (`DocumentClassifiedEto` / `FieldsExtractedEto`) republished as documents reprocess, absorbed idempotently by `(DocumentId, EventType, EventTime)` like any other.

## Out of scope

- **Re-OCR of existing documents.** Text extraction is the most upstream stage and cascades into everything below it; re-running it over existing documents is intentionally not built (the trigger — switching OCR provider — is rare and the cost is highest). Only a *failed* text-extraction run can be retried today (see [pipeline-runs.md](pipeline-runs.md)).

## See also

- [Classification](classification.md) — the pipeline reclassification re-runs, and the per-document `ReclassifyAsync` / `RejectReviewAsync` operator actions
- [Pipeline runs](pipeline-runs.md) — `DocumentPipelineRun` records, including `field-extraction` runs
- [AI provider](../configuration/ai-provider.md) — the LLM client shared by classification and field extraction

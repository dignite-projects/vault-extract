# Integration Events

Dignite Vault Extract publishes document pipeline progress as ABP distributed events (ETOs) — the **EventBus** egress channel, alongside REST, MCP, and Data Download (Webhook planned). Every event payload is thin (an ID plus key metadata); a consumer pulls the details it needs back through REST or MCP after receiving one.

## Stage events

Fired as a document moves through the pipeline. Only `DocumentReadyEto` is gated by the Ready check — the others fire regardless of whether the document ever reaches Ready.

| Event | Wire name | When it fires | Gated by Ready |
| --- | --- | --- | --- |
| `DocumentUploadedEto` | `VaultExtract.Document.Uploaded` | Document upload completed | No |
| `DocumentTextExtractedEto` | `VaultExtract.Document.TextExtracted` | Text extraction completed (image OCR or digital-native) | No |
| `DocumentClassifiedEto` | `VaultExtract.Document.Classified` | Document classification completed | No |
| `DocumentReadyEto` | `VaultExtract.Document.Ready` | Full pipeline complete, no blocking review reason remains | **Yes** |

## Lifecycle events

Orthogonal to the pipeline above — recycle-bin and reclassification transitions, not gated by Ready.

| Event | Wire name | Meaning |
| --- | --- | --- |
| `DocumentDeletedEto` | `VaultExtract.Document.Deleted` | Document soft-deleted (moved to the recycle bin); downstream should archive derived data |
| `DocumentRestoredEto` | `VaultExtract.Document.Restored` | Document restored from the recycle bin; downstream should un-archive |
| `DocumentPermanentlyDeletedEto` | `VaultExtract.Document.PermanentlyDeleted` | Document permanently deleted, including its original file; downstream should physically delete derived data |
| `DocumentReclassifiedToContainerEto` | `VaultExtract.Document.ReclassifiedToContainer` | A previously concrete-typed document was re-recognized as a **container** — its type and type-bound fields are cleared, so downstream should retract the record it derived from the former type. Fires only on a real transition; a fresh upload classified immediately as a container never fires it |

## The Ready gate

`DocumentReadyEto` fires once a document carries **no blocking review reason** — a confirmed type (by automatic classification confidence or operator confirmation), no suspected duplicate, and field extraction not declined for an oversized body. A document that fails the gate is still stored and its earlier stage events still fire; it enters the operator review queue instead of being withheld entirely. Most downstream consumers should subscribe to `DocumentReadyEto` rather than the earlier stage events, since it is the one signal that means "this document's classification and fields are trustworthy."

## Delivery semantics

Dignite Vault Extract delivers every event through ABP's built-in **transactional outbox** — the business change and the event enqueue commit atomically, so events are **at-least-once** and never lost.

Deduplication and ordering are the consumer's responsibility. Every ETO carries `EventTime` (set from the server clock at publish time); a consumer does idempotency by `(DocumentId, EventType, EventTime)` as a high-water mark: discard an incoming event whose `EventTime` is at or before the one already recorded for that key, otherwise apply it and store its `EventTime` as the new mark. A consumer that is itself an ABP application can instead enable the built-in **inbox** (`ConfigureEventInbox()`) for automatic exactly-once consumption by message id.

**`DocumentReadyEto` may fire more than once for the same document** — a pipeline retry, a reclassification, or an operator editing fields on an already-Ready document all re-fire it. Treat it as an **upsert**: the latest `EventTime` wins, not the first delivery.

## Non-guarantees

The stage events before Ready (`DocumentUploadedEto` / `DocumentTextExtractedEto` / `DocumentClassifiedEto`) are observability signals, not a state machine a consumer should drive business logic from: there is **no ordering guarantee** across event types, and a consumer that acts on one of them is acting on **unreviewed** data — the document's type and fields may still change before (or instead of) reaching Ready. `DocumentReadyEto` is the one trusted signal.

## Field notes

- **`DocumentClassifiedEto.ClassificationConfidence = 1.0`** means an operator confirmed the type manually, not that the model was certain.
- **A derived sub-document's `DocumentUploadedEto`** carries `FileName = null`, `FileSize = 0`, `ContentType = null` — it shares its parent's blob rather than owning independent storage. Pull its own descriptors by `DocumentId` through REST/MCP if needed.

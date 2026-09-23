# Integration Events

Dignite Vault Extract publishes document pipeline progress as ABP distributed events (ETOs) — the **EventBus** egress channel, alongside REST, MCP, and Data Download (Webhook planned). Every event payload is thin (an ID plus key metadata); a consumer pulls the details it needs back through REST or MCP after receiving one.

## Stage events

Fired as a document moves through the pipeline. Only `DocumentReadyEto` is gated by the Ready check — the others fire regardless of whether the document ever reaches Ready.

| Event | Wire name | When it fires | Gated by Ready |
| --- | --- | --- | --- |
| `DocumentUploadedEto` | `VaultExtract.Document.Uploaded` | Document upload completed | No |
| `DocumentTextExtractedEto` | `VaultExtract.Document.TextExtracted` | Text extraction completed (image OCR or digital-native). Fires again when an operator re-parses the document from its original file: the Markdown was replaced, so pull it again | No |
| `DocumentClassifiedEto` | `VaultExtract.Document.Classified` | Document classification completed | No |
| `DocumentReadyEto` | `VaultExtract.Document.Ready` | Full pipeline complete, no blocking review reason remains | **Yes** |

## Lifecycle events

Orthogonal to the pipeline above — recycle-bin and reclassification transitions, not gated by Ready.

| Event | Wire name | Meaning |
| --- | --- | --- |
| `DocumentDeletedEto` | `VaultExtract.Document.Deleted` | Document soft-deleted (moved to the recycle bin); downstream should archive derived data. Also fires for each sub-document withdrawn when its parent is re-parsed (it is split again from the new text) or when a bundle is reclassified to a single document type; such a sub-document is not restored later |
| `DocumentRestoredEto` | `VaultExtract.Document.Restored` | Document restored from the recycle bin; downstream should un-archive |
| `DocumentPermanentlyDeletedEto` | `VaultExtract.Document.PermanentlyDeleted` | Document permanently deleted, including its original file; downstream should physically delete derived data |
| `DocumentReclassifiedToContainerEto` | `VaultExtract.Document.ReclassifiedToContainer` | A previously concrete-typed document was re-recognized as a **container** — its type and type-bound fields are cleared, so downstream should retract the record it derived from the former type. Fires only on a real transition; a fresh upload classified immediately as a container never fires it |

## The Ready gate

`DocumentReadyEto` fires once a document carries **no blocking review reason** — a confirmed type (by automatic classification confidence or operator confirmation), no suspected duplicate, and field extraction not declined for an oversized body. A document that fails the gate is still stored and its earlier stage events still fire; it enters the operator review queue instead of being withheld entirely. Most downstream consumers should subscribe to `DocumentReadyEto` rather than the earlier stage events, since it is the one signal that means "this document's classification and fields are trustworthy."

## Delivery semantics

Dignite Vault Extract delivers every event through ABP's built-in **transactional outbox** — the business change and the event enqueue commit atomically, so events are **at-least-once** and never lost.

Deduplication and ordering are the consumer's responsibility. Every ETO carries `EventTime` (set from the server clock at publish time); a consumer does idempotency by `(DocumentId, EventType, EventTime)` as a high-water mark: discard an incoming event whose `EventTime` is at or before the one already recorded for that key, otherwise apply it and store its `EventTime` as the new mark. A consumer that is itself an ABP application can instead enable the built-in **inbox** (`ConfigureEventInbox()`) for automatic exactly-once consumption by message id.

**`DocumentReadyEto` may fire more than once for the same document** — a pipeline retry, a reclassification, a re-parse, or an operator editing fields on an already-Ready document all re-fire it. Treat it as an **upsert**: the latest `EventTime` wins, not the first delivery.

## Compatibility

The wire names and payload shapes on this page are stable from the 0.5.0 line. Adding an optional property is non-breaking; renaming a wire name or removing a property is a BREAKING entry in `CHANGELOG.md` with a migration note, and is preceded by a GitHub Issue. Every ETO carries `Version = "1.0"`; it is informational today — do not branch on it — and changes only alongside a breaking payload change announced in the CHANGELOG.

## Non-guarantees

The stage events before Ready (`DocumentUploadedEto` / `DocumentTextExtractedEto` / `DocumentClassifiedEto`) are observability signals, not a state machine a consumer should drive business logic from: there is **no ordering guarantee** across event types, and a consumer that acts on one of them is acting on **unreviewed** data — the document's type and fields may still change before (or instead of) reaching Ready. `DocumentReadyEto` is the one trusted signal.

## Field notes

- **`DocumentClassifiedEto.ClassificationConfidence = 1.0`** is the value stamped when the type was declared by the uploader (`UploadDocumentInput.DocumentTypeId`, #623) or confirmed by an operator. The LLM classifier can also legitimately return 1.0 (its score is normalised but not clamped below 1.0), so this value alone does not prove a human verified the type.
- **A derived sub-document's `DocumentUploadedEto`** carries `FileName = null`, `FileSize = 0`, `ContentType = null` — it shares its parent's blob rather than owning independent storage. Pull its own descriptors by `DocumentId` through REST/MCP if needed.

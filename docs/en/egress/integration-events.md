```json
//[doc-seo]
{
    "Description": "Subscribe to Dignite Vault Extract's distributed events: stage and lifecycle ETOs, payload properties, the Ready gate and at-least-once delivery semantics."
}
```

# Integration Events

Dignite Vault Extract publishes document pipeline progress as ABP [distributed events](https://abp.io/docs/latest/framework/infrastructure/event-bus/distributed) (ETOs). This is the **EventBus** egress channel, next to the [REST API](rest-api.md), the [MCP server](mcp-server.md) and [Data Download](data-download.md); a Webhook channel is planned. Every payload is thin (an ID plus key metadata): a consumer pulls the details it needs back through REST or MCP after receiving an event.

## Subscribing

The ETO classes live in the `Dignite.Vault.Extract.Abstractions` package (namespace `Dignite.Vault.Extract.Abstractions.Documents`), which depends only on ABP base packages. A consumer references that package, configures the same distributed event bus provider as the Vault Extract host (RabbitMQ, Kafka, Azure Service Bus, and so on), and handles the events it needs:

```csharp
using Dignite.Vault.Extract.Abstractions.Documents;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;

public class DocumentReadyHandler
    : IDistributedEventHandler<DocumentReadyEto>, ITransientDependency
{
    public async Task HandleEventAsync(DocumentReadyEto eventData)
    {
        // Route by (eventData.TenantId, eventData.DocumentTypeCode), then pull
        // the Markdown and field values through REST or MCP by eventData.DocumentId.
        // Treat the event as an upsert: keep the latest EventTime per DocumentId.
    }
}
```

Each event's wire name (below) is set with `[EventName]`, so a non-.NET consumer can subscribe to the same names.

> The shipped host configures the transactional outbox and inbox on its `DbContext` but references **no broker package**. Without one, ABP's distributed event bus runs in process and only handlers inside the host receive the events. To reach a separate consumer, add an ABP event bus provider to the host (for example `Volo.Abp.EventBus.RabbitMQ` with its `AbpEventBusRabbitMqModule`) and configure it on both sides.

## Stage Events

Fired as a document moves through the pipeline. Only `DocumentReadyEto` is gated by the Ready check — the others fire regardless of whether the document ever reaches Ready.

| Event | Wire name | When it fires | Gated by Ready |
| --- | --- | --- | --- |
| `DocumentUploadedEto` | `VaultExtract.Document.Uploaded` | Document upload completed | No |
| `DocumentTextExtractedEto` | `VaultExtract.Document.TextExtracted` | Text extraction completed (image OCR or digital-native). Fires again when an operator re-parses the document from its original file: the Markdown was replaced, so pull it again | No |
| `DocumentClassifiedEto` | `VaultExtract.Document.Classified` | Document classification completed | No |
| `DocumentReadyEto` | `VaultExtract.Document.Ready` | Full pipeline complete, no blocking review reason remains | **Yes** |

## Lifecycle Events

Orthogonal to the pipeline above — recycle-bin and reclassification transitions, not gated by Ready.

| Event | Wire name | Meaning |
| --- | --- | --- |
| `DocumentDeletedEto` | `VaultExtract.Document.Deleted` | Document soft-deleted (moved to the recycle bin); downstream should archive derived data. Also fires for each sub-document withdrawn when its parent is re-parsed (it is split again from the new text) or when a bundle is reclassified to a single document type; such a sub-document is not restored later |
| `DocumentRestoredEto` | `VaultExtract.Document.Restored` | Document restored from the recycle bin; downstream should un-archive |
| `DocumentPermanentlyDeletedEto` | `VaultExtract.Document.PermanentlyDeleted` | Document permanently deleted, including its original file; downstream should physically delete derived data |
| `DocumentReclassifiedToContainerEto` | `VaultExtract.Document.ReclassifiedToContainer` | A previously concrete-typed document was re-recognized as a **container** — its type and type-bound fields are cleared, so downstream should retract the record it derived from the former type. Fires only on a real transition; a fresh upload classified immediately as a container never fires it |

## Payloads

Every ETO carries the same four properties; three carry more:

| Property | Type | On | Description |
| --- | --- | --- | --- |
| `Version` | `string` | All | `"1.0"`. Informational; see [Compatibility](#compatibility) |
| `DocumentId` | `Guid` | All | The document the event is about |
| `TenantId` | `Guid?` | All | The document's tenant; `null` for a Host document |
| `EventTime` | `DateTime` | All | Server clock at publish time; the idempotency high-water mark |
| `FileName` | `string?` | `DocumentUploadedEto` | Original file name; `null` for a sub-document |
| `FileSize` | `long` | `DocumentUploadedEto` | Size in bytes; `0` for a sub-document |
| `ContentType` | `string?` | `DocumentUploadedEto` | MIME type; `null` for a sub-document |
| `DocumentTypeCode` | `string` | `DocumentClassifiedEto` | The classified type's code |
| `ClassificationConfidence` | `double` | `DocumentClassifiedEto` | `0.0` to `1.0`; see [Field Notes](#field-notes) |
| `DocumentTypeCode` | `string?` | `DocumentReadyEto` | The confirmed type's code |
| `OriginDocumentId` | `Guid?` | `DocumentReadyEto` | For a [sub-document](../documents/sub-documents.md), the source document it was split from; `null` otherwise |

A type code is unique only within its layer, so route by the `(TenantId, DocumentTypeCode)` pair, never by the code alone.

## The Ready Gate

`DocumentReadyEto` fires once a document carries **no blocking review reason** — a confirmed type (by automatic classification confidence or operator confirmation), no suspected duplicate, and field extraction not declined for an oversized body. A document that fails the gate is still stored and its earlier stage events still fire; it enters the operator review queue instead of being withheld entirely. Most downstream consumers should subscribe to `DocumentReadyEto` rather than the earlier stage events, since it is the one signal that means "this document's classification and fields are trustworthy."

## Delivery Semantics

Dignite Vault Extract delivers every event through ABP's built-in **transactional outbox** — the business change and the event enqueue commit atomically, so events are **at-least-once** and never lost.

Deduplication and ordering are the consumer's responsibility. Every ETO carries `EventTime` (set from the server clock at publish time); a consumer does idempotency by `(DocumentId, EventType, EventTime)` as a high-water mark: discard an incoming event whose `EventTime` is at or before the one already recorded for that key, otherwise apply it and store its `EventTime` as the new mark. A consumer that is itself an ABP application can instead enable the built-in **inbox** (`ConfigureEventInbox()`) for automatic exactly-once consumption by message id.

**`DocumentReadyEto` may fire more than once for the same document** — a pipeline retry, a reclassification, a re-parse, or an operator editing fields on an already-Ready document all re-fire it. Treat it as an **upsert**: the latest `EventTime` wins, not the first delivery.

A **container** document (a bundle of several documents) never fires `DocumentReadyEto`; its sub-documents fire it instead, each carrying `OriginDocumentId`. See [Sub-Documents](../documents/sub-documents.md).

## Compatibility

The wire names and payload shapes on this page are stable from the 0.5.0 line. Adding an optional property is non-breaking; renaming a wire name or removing a property is a BREAKING entry in `CHANGELOG.md` with a migration note, and is preceded by a GitHub Issue. Every ETO carries `Version = "1.0"`; it is informational today — do not branch on it — and changes only alongside a breaking payload change announced in the CHANGELOG.

## Non-guarantees

The stage events before Ready (`DocumentUploadedEto` / `DocumentTextExtractedEto` / `DocumentClassifiedEto`) are observability signals, not a state machine a consumer should drive business logic from: there is **no ordering guarantee** across event types, and a consumer that acts on one of them is acting on **unreviewed** data — the document's type and fields may still change before (or instead of) reaching Ready. `DocumentReadyEto` is the one trusted signal.

## Field Notes

- **`DocumentClassifiedEto.ClassificationConfidence = 1.0`** is the value stamped when the type was declared by the uploader (`UploadDocumentInput.DocumentTypeId`, #623) or confirmed by an operator. The LLM classifier can also legitimately return 1.0 (its score is normalised but not clamped below 1.0), so this value alone does not prove a human verified the type.
- **A derived sub-document's `DocumentUploadedEto`** carries `FileName = null`, `FileSize = 0`, `ContentType = null` — it shares its parent's blob rather than owning independent storage. Pull its own descriptors by `DocumentId` through REST/MCP if needed.

## See Also

- [Documents and Review](../documents/documents.md) — lifecycle statuses and the review reasons behind the Ready gate
- [REST API](rest-api.md) — pulling a document's details after an event
- [Operator Notifications](operator-notifications.md) — in-app notifications for the uploader (not part of this contract)
- [ABP Distributed Event Bus](https://abp.io/docs/latest/framework/infrastructure/event-bus/distributed)

---
description: "Document AI egress event contracts: multi-stage event table, lifecycle events, Ready gate, at-least-once + EventTime idempotency delivery semantics"
paths:
  - "**/*Eto.cs"
  - "**/*EventHandler*.cs"
  - "**/*IntegrationEvent*.cs"
---

# Egress Event Contract Details (Document AI)

> Carried over from CLAUDE.md, auto-loaded when editing integration-event ETOs / EventHandlers. CLAUDE.md keeps only the egress list, the event-name sequence, the Ready-gate core paragraph, and the one-line delivery semantics.

## Egress event contracts (multi-stage + thin payload)

| Stage event | Trigger | Gated by Ready |
|---------|---------|----------------|
| `DocumentUploadedEto` | document upload completed | No |
| `OCRCompletedEto` | OCR completed (payload carries the `UsedOcr` path marker) | No |
| `DocumentClassifiedEto` | document classification completed | No |
| `FieldsExtractedEto` | field extraction completed (single-segment payload with `FieldCount`; `Document.TenantId` decides which layer's schema this document runs; single-bucket persistence) | No |
| `DocumentReadyEto` | **full pipeline complete + a confirmed type obtained (classification confidence met or manual confirmation)** | **Yes** |

**Recycle-bin / lifecycle events** (orthogonal to the multi-stage pipeline, not gated by Ready; downstream archives or physically deletes derived data accordingly):

| Lifecycle event | Trigger |
|------------|---------|
| `DocumentDeletedEto` | document soft-deleted (into recycle bin) — downstream should set derived data to a recoverable archived state |
| `DocumentRestoredEto` | document restored from recycle bin — downstream should un-archive |
| `DocumentPermanentlyDeletedEto` | document permanently deleted (including the original file / archive blob) — downstream should physically delete derived data |

**Payload design**: event payloads are uniformly thin (ID + key metadata); downstream pulls detailed data back via REST/MCP.

## Ready gate (classification confidence + manual review)

- **Design intent**: only a document whose type is confirmed is a trustworthy signal downstream — documents the classifier is unsure about are not auto-released
- **Gate enforcement point**: **only `DocumentReadyEto` is gated** — early-stage events still fire, but primary downstream consumers subscribe to `DocumentReadyEto` by default
- **Gate criterion**: a document must obtain a confirmed `DocumentTypeCode` (auto-classification confidence ≥ that type's `ConfidenceThreshold`, or operator manual confirmation); `DeriveLifecycle` does not transition to Ready when the type is empty
- **Documents that fail the gate**: still stored (not lost, not deleted); early-stage events still published; `DocumentReadyEto` is withheld for now; the document enters the "pending manual review queue" in the operator UI; the operator Reclassify-assigns a type / Rejects — on passing, `DocumentReadyEto` is triggered
- **OCR-confidence threshold + signal fields have both been removed** (#196): OCR average confidence does not predict real quality (a whole page scanned crooked / blurry / with messy layout is not necessarily reflected in the average). The threshold config (host default + per-tenant override) has been removed; the `OcrConfidence` signal itself (aggregate-root field + exposure on `OCRCompletedEto` / `DocumentReadyEto` + OCR-provider computation) has been removed as well — since the average does not predict quality, passing it downstream for secondary gating is equally ineffective, so keeping it would be just a dead signal. If OCR quality ops-monitoring is needed in future, it is redesigned via OCR-provider telemetry / `PipelineRun` diagnostics per a population-statistics requirement, not reserved in the channel egress contract; `OCRCompletedEto.UsedOcr` (a path fact, not a quality prediction) is retained

## Delivery semantics: at-least-once + monotonic-timestamp idempotency

- **Reliability commitment**: Document AI delivers events via **ABP's built-in transactional outbox** — the business change and the event enqueue are atomically persisted within the same UoW (written to `AbpEventOutbox`), and a background worker scans the table to actually publish. **Events are never lost** (at-least-once)
- **Dedup / replacement is the downstream consumer's responsibility**: the channel layer maintains **no** event state table, does **no** "in-flight replacement", and does **not** wait for a downstream ack. Document AI doing idempotency on the downstream's behalf would break the downstream's audit chain + add channel complexity, violating the channel philosophy
- **Downstream consumer idempotency rule**: every ETO carries `EventTime: DateTime` (filled with `Clock.Now` at Document AI publish time). The downstream consumer does idempotency by `(DocumentId, EventType, EventTime)`:
  - already-processed `EventTime <= incoming.EventTime` → discard incoming (at-least-once redelivery)
  - already-processed `EventTime > incoming.EventTime` → discard incoming (a stale event arriving out of order)
  - otherwise apply incoming → persist `EventTime` as the high-water mark for that key
- **Design trade-off**: giving up "in-flight replacement" in exchange for channel-layer simplification. Repeated triggers on the same document (e.g. OCR retry, operator reclassify) make downstream receive N events, naturally discarding old versions by EventTime — consistent with the downstream business consumer's existing idempotency implementation path (e.g. the common message-queue consumer pattern), solvable downstream with a single `WHERE Version >` line
- **If the downstream is also an ABP project**: it can enable ABP's built-in inbox (`builder.ConfigureEventInbox()`), consuming exactly-once automatically by `MessageId`, sparing even the `EventTime` comparison

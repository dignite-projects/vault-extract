using System;
using Volo.Abp.EventBus;

namespace Dignite.DocumentAI.Abstractions.Documents;

/// <summary>
/// Published when a previously concrete-typed document is re-recognized as a <b>container</b> (#355): its type
/// (and any type-bound field values) is cleared, so any business record a downstream consumer derived from its
/// former <see cref="DocumentClassifiedEto"/> / <see cref="DocumentReadyEto"/> is now invalid and must be
/// <b>retracted</b>. The document itself is not deleted — it lives on as a container whose constituents become
/// independent sub-documents (read them by querying <c>OriginDocumentId == this DocumentId</c>).
/// <para>
/// This is the type→container mirror of the container→type retraction (#349), which reuses
/// <see cref="DocumentDeletedEto"/> for the spawned sub-documents. Not gated by the Ready gate: the retraction
/// must reach downstream regardless of the container's confidence. A fresh upload first classified as a container
/// fires nothing (there was no prior typed record to retract).
/// </para>
/// <para>
/// Thin payload (the channel philosophy): downstream retracts the record keyed by <see cref="DocumentId"/> and
/// pulls any current detail back via REST/MCP. Idempotency under at-least-once delivery uses
/// <c>(DocumentId, EventType, EventTime)</c>.
/// </para>
/// </summary>
[EventName("DocumentAI.Document.ReclassifiedToContainer")]
public class DocumentReclassifiedToContainerEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// Event occurrence time. DocumentAI fills it with <see cref="Volo.Abp.Timing.IClock.Now"/> at publish time.
    /// Downstream consumers can use <c>(DocumentId, EventType, EventTime)</c> for idempotence under at-least-once delivery.
    /// </summary>
    public required DateTime EventTime { get; init; }
}

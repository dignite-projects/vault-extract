using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published after the full pipeline completes and the document has a confirmed type: the "trusted
/// signal" for downstream consumers. A lifecycle transition to <c>Ready</c> implicitly means the
/// classification / manual-review gate passed:
/// <list type="bullet">
///   <item>Automatic classification confidence meets the type threshold: automatically reaches Ready
///   and publishes.</item>
///   <item>Classification confidence is insufficient / no suitable type: the document enters the
///   manual review queue and publishes only after an operator confirms the type.</item>
/// </list>
/// Most downstream business consumers should subscribe to this event rather than early-stage events
/// (DocumentUploaded/OCRCompleted/...).
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("Extract.Document.Ready")]
public class DocumentReadyEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// Event occurrence time. Extract fills it with <see cref="Volo.Abp.Timing.IClock.Now"/> at
    /// publish time. Downstream consumers can use <c>(DocumentId, EventType, EventTime)</c> for
    /// idempotence under at-least-once delivery.
    /// </summary>
    public required DateTime EventTime { get; init; }

    public string? DocumentTypeCode { get; init; }

    /// <summary>
    /// Provenance link for a Scenario B sub-document (#306): when this document was derived from a constituent
    /// of another document, the id of that source document; <c>null</c> for normally-uploaded documents.
    /// Downstream may follow it to associate the derived document with its source. New optional field; defaults
    /// to <c>null</c> for producers / consumers that predate it.
    /// </summary>
    public Guid? OriginDocumentId { get; init; }
}

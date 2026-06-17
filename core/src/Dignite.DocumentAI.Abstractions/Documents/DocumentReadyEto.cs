using System;
using Volo.Abp.EventBus;

namespace Dignite.DocumentAI.Abstractions.Documents;

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
[EventName("DocumentAI.Document.Ready")]
public class DocumentReadyEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// Event occurrence time. DocumentAI fills it with <see cref="Volo.Abp.Timing.IClock.Now"/> at
    /// publish time. Downstream consumers can use <c>(DocumentId, EventType, EventTime)</c> for
    /// idempotence under at-least-once delivery.
    /// </summary>
    public required DateTime EventTime { get; init; }

    /// <summary>
    /// Confirmed document type, or <c>null</c>. Since #346, <c>null</c> together with <see cref="IsContainer"/> =
    /// <c>true</c> is a valid Ready outcome: a container has no single type. Downstream must tolerate this pairing
    /// and skip building a record from the container.
    /// </summary>
    public string? DocumentTypeCode { get; init; }

    /// <summary>
    /// Whether this document is a <b>container</b> (#346): a parent bundling several independent documents that
    /// ran no type-bound field extraction itself. When <c>true</c>, <see cref="DocumentTypeCode"/> is <c>null</c>
    /// and downstream must <b>not</b> build a business record from this document — the real records are its
    /// sub-documents (each fires its own <c>DocumentReadyEto</c> carrying <see cref="OriginDocumentId"/>). New
    /// optional field; defaults to <c>false</c> for producers / consumers that predate it.
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>
    /// Provenance link for a Scenario B sub-document (#306): when this document was derived from a constituent
    /// of another document, the id of that source document; <c>null</c> for normally-uploaded documents.
    /// Downstream may follow it to associate the derived document with its source. New optional field; defaults
    /// to <c>null</c> for producers / consumers that predate it.
    /// </summary>
    public Guid? OriginDocumentId { get; init; }
}

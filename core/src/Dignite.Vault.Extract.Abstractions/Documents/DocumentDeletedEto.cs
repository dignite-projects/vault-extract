using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published when a document is soft-deleted and enters the recycle bin.
/// Downstream business consumers should put their derived data into a recoverable archived state:
/// restore it after <see cref="DocumentRestoredEto"/>, or physically delete it only after
/// <see cref="DocumentPermanentlyDeletedEto"/>.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("Extract.Document.Deleted")]
public class DocumentDeletedEto
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
}

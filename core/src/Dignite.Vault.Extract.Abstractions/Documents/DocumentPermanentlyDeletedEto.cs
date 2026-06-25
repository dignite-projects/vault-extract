using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published when a document is permanently deleted (physical delete).
/// Difference from <see cref="DocumentDeletedEto"/> (soft-delete signal): soft delete can be restored,
/// so business modules should put their own data into a recoverable archived state. Permanent delete
/// cannot be restored, so business modules should physically delete data derived from this document,
/// such as contracts or extracted fields.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.PermanentlyDeleted")]
public class DocumentPermanentlyDeletedEto
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

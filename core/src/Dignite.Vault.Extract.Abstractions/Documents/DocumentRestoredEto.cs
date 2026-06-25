using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published when a document is restored from the recycle bin.
/// Downstream business consumers should restore data they previously archived because of
/// <see cref="DocumentDeletedEto"/>.
/// <para>
/// Immutable contract (issue #188): all properties are <c>init</c>-only, and
/// <see cref="EventTime"/> is marked <c>required</c>.
/// </para>
/// </summary>
[EventName("Extract.Document.Restored")]
public class DocumentRestoredEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// Event occurrence time, filled by Extract from <see cref="Volo.Abp.Timing.IClock.Now"/>
    /// when publishing.
    /// Downstream consumers use <c>(DocumentId, EventType, EventTime)</c> for idempotency under
    /// at-least-once delivery.
    /// </summary>
    public required DateTime EventTime { get; init; }
}

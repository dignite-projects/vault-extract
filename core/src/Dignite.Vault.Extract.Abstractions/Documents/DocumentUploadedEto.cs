using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published after document upload completes (DB row + blob persisted); this is the start of the
/// channel pipeline. Thin payload: downstream consumers pull detailed data back through REST / MCP.
/// Not constrained by the Ready gate.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only because ETOs are event payloads
/// and immutable after publication. <see cref="EventTime"/> is marked <c>required</c>, forcing object
/// initializers at compile time and preventing default(DateTime) risk.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.Uploaded")]
public class DocumentUploadedEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// Event occurrence time. Extract fills it with <see cref="Volo.Abp.Timing.IClock.Now"/> at
    /// publish time. Downstream consumers can use <c>(DocumentId, EventType, EventTime)</c> for
    /// idempotence: discard an event when a later EventTime has already been processed for the same
    /// key. Designed for ABP's built-in transactional outbox with at-least-once delivery.
    /// </summary>
    public required DateTime EventTime { get; init; }

    public string? FileName { get; init; }

    public long FileSize { get; init; }

    public string? ContentType { get; init; }
}

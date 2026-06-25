using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published after document classification completes. Downstream field extraction / business
/// consumers route by <see cref="DocumentTypeCode"/>. Thin payload: body Markdown is pulled back
/// through REST / MCP / repository and is not delivered with the event.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.Classified")]
public class DocumentClassifiedEto
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

    public string DocumentTypeCode { get; init; } = default!;

    public double ClassificationConfidence { get; init; }
}

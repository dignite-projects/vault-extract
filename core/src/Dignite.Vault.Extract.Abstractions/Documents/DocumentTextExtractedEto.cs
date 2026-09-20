using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published once per completed text extraction, whether image OCR or digital-native. Thin payload:
/// consumers pull Markdown and extraction provenance (<c>DocumentParseMetadata.ProviderName</c>) back
/// through REST. An observability signal, not a state-machine input — at-least-once, no ordering
/// guarantee relative to other event types.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.TextExtracted")]
public class DocumentTextExtractedEto
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

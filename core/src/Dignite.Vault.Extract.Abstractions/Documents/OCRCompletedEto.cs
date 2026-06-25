using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published after text extraction (OCR or digital-native extraction) completes.
/// <see cref="UsedOcr"/> marks whether image OCR or digital-native direct extraction was used.
/// Downstream consumers pull Markdown back through REST.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.OCRCompleted")]
public class OCRCompletedEto
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

    /// <summary>
    /// Whether OCR was actually used: true = image OCR; false = digital-native direct extraction.
    /// </summary>
    public bool UsedOcr { get; init; }

    /// <summary>
    /// Number of embedded-image OCR calls <b>dispatched</b> via figure-OCR (#306) — counting every call sent to
    /// <c>IOcrProvider</c>, <b>including ones that threw</b> (a failed call may still incur provider cost), so this
    /// counts dispatched attempts, not successful transcriptions. <see cref="UsedOcr"/> still means "scan vs
    /// digital" (a path fact); this is the named figure-OCR signal so downstream cost-attribution can see that
    /// embedded-image OCR occurred on a digital document. 0 when none ran. New optional field; defaults to 0 for
    /// producers / consumers that predate it.
    /// </summary>
    public int FigureOcrCount { get; init; }
}

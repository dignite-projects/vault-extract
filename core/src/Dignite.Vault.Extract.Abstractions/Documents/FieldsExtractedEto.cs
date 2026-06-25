using System;
using Volo.Abp.EventBus;

namespace Dignite.Vault.Extract.Abstractions.Documents;

/// <summary>
/// Published after the field extraction pipeline completes. Unified replacement for v1
/// <c>MetadataExtractedEto</c> (system / host fields) and <c>CustomFieldsExtractedEto</c> (tenant
/// fields) under field architecture v2 + Interpretation X: one LLM extraction runs only one layer of
/// field definitions, decided by Document.TenantId as host vs tenant, so one event can express "all
/// extractable fields for this document were persisted". Downstream consumers can distinguish
/// scenarios by <see cref="TenantId"/>. Thin payload: concrete field values are pulled back through
/// REST / MCP.
/// <para>
/// Stable contract (issue #188): all properties are <c>init</c>-only; <see cref="EventTime"/> is
/// marked <c>required</c>.
/// </para>
/// </summary>
[EventName("VaultExtract.Document.FieldsExtracted")]
public class FieldsExtractedEto
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
    /// Number of non-empty fields produced by this extraction, from the FieldDefinition layer that
    /// belongs to this Document.
    /// </summary>
    public int FieldCount { get; init; }
}

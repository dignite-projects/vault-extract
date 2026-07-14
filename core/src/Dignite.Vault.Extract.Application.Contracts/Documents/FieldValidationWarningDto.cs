using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// One field validation warning for the operator UI (#527 §10): a type-bound field's extracted value failed a
/// validation rule declared in its prompt. The value itself stays on <c>ExtractedFields</c> (unchanged); this carries
/// the separate, escaped warning text, resolved to the field's current name / display name (#207, so renames reflect).
/// Exposed only on the REST detail surface (through <see cref="ReviewReasonDetailDto"/>) — never in field-value search,
/// CSV/XLSX export, or ETO payloads (#527 §11).
/// </summary>
public class FieldValidationWarningDto
{
    /// <summary>Immutable id of the field this warning is attached to (#207).</summary>
    public Guid FieldDefinitionId { get; set; }

    /// <summary>Current field name; <c>null</c> if the field definition was removed.</summary>
    public string? FieldName { get; set; }

    /// <summary>Current field display name; <c>null</c> if the field definition was removed.</summary>
    public string? FieldDisplayName { get; set; }

    /// <summary>Concise validation message. The client renders it as escaped text, never as HTML.</summary>
    public string Message { get; set; } = default!;
}

using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export template column input (#207 converged to ExtractedField-only). The caller submits the
/// immutable field definition id; AppService validates that it belongs to the template's
/// <c>DocumentTypeId</c> before persisting. Column titles are resolved dynamically from
/// <c>FieldDefinition.DisplayName</c> during export and need no configuration.
/// System fields are exported fixedly and are not configured here.
/// </summary>
public class ExportColumnInput
{
    /// <summary>Immutable id of the type-bound field definition to export; it must belong to the template's document type.</summary>
    [Required]
    public Guid FieldDefinitionId { get; set; }

    public int Order { get; set; }
}

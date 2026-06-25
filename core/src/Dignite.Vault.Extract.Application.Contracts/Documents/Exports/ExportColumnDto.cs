using System;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// One export template column (#207 converged to ExtractedField-only). System fields
/// (LifecycleStatus / ReviewStatus / Title) are emitted fixedly by the export engine and are not listed
/// here. Column titles are resolved dynamically from <c>FieldDefinition.DisplayName</c> during export.
/// </summary>
public class ExportColumnDto
{
    /// <summary>Immutable id of the referenced type-bound field definition (#207: internal stable handle; field names can be renamed by admins and are not used as reference keys).</summary>
    public Guid FieldDefinitionId { get; set; }

    /// <summary>Column order in the output, ascending.</summary>
    public int Order { get; set; }
}

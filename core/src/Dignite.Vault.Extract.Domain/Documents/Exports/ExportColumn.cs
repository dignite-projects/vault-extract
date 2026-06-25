using System;
using Volo.Abp;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// One column definition in an export template (value object, #207 narrowed to ExtractedField-only).
/// Each column references one type-bound field value by immutable <see cref="FieldDefinitionId"/>.
/// The output file column header is taken from <c>FieldDefinition.DisplayName</c> during export, so it
/// does not need to be configured separately on template columns and automatically follows field
/// renames.
/// <para>
/// Generic system fields (<c>LifecycleStatus</c> / <c>ReviewStatus</c> / <c>Title</c>) are
/// <b>always emitted</b> by the export engine and do not go through template-column configuration.
/// They are stable Extract metadata contracts and do not need configuration like business fields
/// (#207).
/// </para>
/// <para>
/// Serialized as part of <see cref="ExportTemplate.Columns"/> into a large text column. Get-only
/// properties plus a single parameterized constructor let System.Text.Json reuse the same constructor
/// during deserialization, with parameter names matching property names. Constructor validation reruns
/// on DB round-trip, where stored data should already be valid.
/// </para>
/// </summary>
public class ExportColumn
{
    /// <summary>
    /// Referenced type-bound field value (<c>FieldDefinition.Id</c>, #207). Resolved by AppService on
    /// save from <c>(DocumentTypeId, fieldName)</c>; output / admin UI later joins the current
    /// <c>FieldDefinition.Name</c>. <c>FieldDefinition.Name</c> rename does not affect this reference.
    /// </summary>
    public Guid FieldDefinitionId { get; }

    /// <summary>Column order in the output, ascending.</summary>
    public int Order { get; }

    public ExportColumn(Guid fieldDefinitionId, int order)
    {
        FieldDefinitionId = Check.NotDefaultOrNull<Guid>(fieldDefinitionId, nameof(fieldDefinitionId));
        Order = order;
    }
}

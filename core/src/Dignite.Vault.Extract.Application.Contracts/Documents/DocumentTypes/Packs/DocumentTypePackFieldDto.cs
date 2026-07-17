using System.ComponentModel.DataAnnotations;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// One field definition inside a <see cref="DocumentTypePackDto"/>. Mirrors the mutable shape of a
/// <c>FieldDefinition</c> minus identity/layer (resolved on import from the caller's layer + owning type).
/// Matched on import by (owning type, <see cref="Name"/>).
/// </summary>
public class DocumentTypePackFieldDto
{
    [Required]
    [RegularExpression(FieldDefinitionConsts.NamePattern)]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    public string? Prompt { get; set; }

    public FieldDataType DataType { get; set; } = FieldDataType.Text;

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }

    public bool AllowMultiple { get; set; }

    public bool IsUniqueKey { get; set; }
}

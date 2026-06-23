using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Extract.Documents.Fields;

public class CreateFieldDefinitionDto
{
    /// <summary>Immutable parent document type id (#207: creates the FieldDefinition.DocumentTypeId FK and binds by id rather than renameable TypeCode).</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    /// <summary>Extraction instruction, <b>optional</b>. When blank, the LLM infers what to extract from <see cref="Name"/> and <see cref="DataType"/> only.</summary>
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string? Prompt { get; set; }

    public FieldDataType DataType { get; set; } = FieldDataType.Text;

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>Whether multiple values are allowed (#212). Only <see cref="FieldDataType.Text"/> fields may be true; enabling it for non-text fields fails loudly at the entity layer.</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>Whether this field is part of the type's duplicate-detection unique key (#411). The normalized values of all unique-key fields are hashed into the document's fingerprint to flag duplicate re-uploads.</summary>
    public bool IsUniqueKey { get; set; }
}

using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.Fields;

public class FieldDefinitionDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }

    /// <summary>Immutable id of the owning document type (#207: internal stable handle; TypeCode can be renamed by admins and is not used as a reference key).</summary>
    public Guid DocumentTypeId { get; set; }
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Prompt { get; set; }
    public FieldDataType DataType { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>Whether multiple values are allowed (#212). When true, only <see cref="FieldDataType.Text"/> field output is rendered as a JSON array.</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>Whether this field is part of the type's duplicate-detection unique key (#411). The normalized values of all unique-key fields are hashed into the document's fingerprint to flag duplicate re-uploads.</summary>
    public bool IsUniqueKey { get; set; }
}

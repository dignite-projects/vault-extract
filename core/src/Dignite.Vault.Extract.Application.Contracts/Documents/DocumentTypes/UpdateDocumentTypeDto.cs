using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public class UpdateDocumentTypeDto
{
    /// <summary>Type machine code. Renames are allowed since #207; regex allowlisting is enforced by the entity, and same-layer (TenantId, TypeCode) uniqueness is enforced by AppService.</summary>
    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string TypeCode { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    /// <summary>Optional classification helper description (#262): only helps AI identify this type and does not participate in document content post-processing.</summary>
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDescriptionLength))]
    public string? Description { get; set; }

    [Range(0d, 1d)]
    public double ConfidenceThreshold { get; set; }

    public int Priority { get; set; }

    /// <summary>
    /// What counts as a duplicate for this type (#651). <see cref="DuplicateDetectionScope.Layer"/> — the whole
    /// layer + type, whoever uploaded — is the default and the pre-#651 behaviour;
    /// <see cref="DuplicateDetectionScope.Uploader"/> narrows a collision to documents sharing the subject's
    /// uploader. The enum's integer values are a frozen serialized contract.
    /// </summary>
    [EnumDataType(typeof(DuplicateDetectionScope))]
    public DuplicateDetectionScope DuplicateScope { get; set; } = DuplicateDetectionScope.Layer;
}

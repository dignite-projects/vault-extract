using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// A portable, declarative snapshot of one <c>DocumentType</c> plus its <c>FieldDefinition</c>s — the unit
/// of config import/export (#444). It carries NO identity or layer: <see cref="TypeCode"/> is the match key
/// and the target layer is the caller's current layer, so the same pack applies cleanly to any deployment
/// or tenant. Business-neutral by design — the channel ships no bundled packs (packs are consumer/community
/// content authored against this mechanism).
/// </summary>
public class DocumentTypePackDto
{
    /// <summary>Pack schema version. Only <see cref="DocumentTypePackConsts.CurrentVersion"/> is accepted on
    /// import; a newer/unknown version loud-fails rather than silently mis-mapping.</summary>
    public int Version { get; set; } = DocumentTypePackConsts.CurrentVersion;

    [Required]
    [RegularExpression(DocumentTypeConsts.TypeCodePattern)]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string TypeCode { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDescriptionLength))]
    public string? Description { get; set; }

    [Range(0d, 1d)]
    public double ConfidenceThreshold { get; set; } = 0.7;

    public int Priority { get; set; }

    /// <summary>
    /// What counts as a duplicate for this type (#651). <see cref="DuplicateDetectionScope.Layer"/> — the whole
    /// layer + type, whoever uploaded — is the default and the pre-#651 behaviour;
    /// <see cref="DuplicateDetectionScope.Uploader"/> narrows a collision to documents sharing the subject's
    /// uploader. The enum's integer values are a frozen serialized contract.
    /// </summary>
    [EnumDataType(typeof(DuplicateDetectionScope))]
    public DuplicateDetectionScope DuplicateScope { get; set; } = DuplicateDetectionScope.Layer;

    public List<DocumentTypePackFieldDto> Fields { get; set; } = new();
}

using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public class CreateDocumentTypeDto
{
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
    public double ConfidenceThreshold { get; set; } = 0.7;

    public int Priority { get; set; }
}

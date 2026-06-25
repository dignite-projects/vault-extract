using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents.Reprocessing;

/// <summary>
/// Bulk field re-extraction preview (#289 scenario two): affected document count plus the current
/// field list for that type, so users know which fields will be re-extracted.
/// </summary>
public class FieldReextractionPreviewDto
{
    public Guid DocumentTypeId { get; set; }

    /// <summary>Number of documents under this type with completed text extraction that will be re-extracted (current layer, excluding recycle bin).</summary>
    public long DocumentCount { get; set; }

    /// <summary>Current active field definition names for this type, ordered by DisplayOrder; preview shows which fields will be extracted.</summary>
    public List<string> FieldNames { get; set; } = new();
}

/// <summary>Input for triggering bulk field re-extraction: fixed to a document-type scope, a leaf operation with no cascade and no destructive classification side effects.</summary>
public class StartFieldReextractionInput
{
    [Required]
    public Guid DocumentTypeId { get; set; }
}

/// <summary>Bulk reclassification preview (#289 scenario one): affected document count. The frontend renders warning copy based on scope + toggle combinations.</summary>
public class ReclassificationPreviewDto
{
    public long DocumentCount { get; set; }
}

/// <summary>
/// Scope input for bulk reclassification, shared by preview and trigger. A human chooses the scope;
/// the system provides no default.
/// <para>
/// Validation: <see cref="ReclassificationScope.OnlyCurrentType"/> requires
/// <see cref="DocumentTypeId"/>; other scopes ignore <see cref="DocumentTypeId"/>.
/// </para>
/// </summary>
public class ReclassificationScopeInput : IValidatableObject
{
    [Required]
    public ReclassificationScope Scope { get; set; }

    /// <summary>Required only for <see cref="ReclassificationScope.OnlyCurrentType"/>, anchoring documents currently classified as that type only.</summary>
    public Guid? DocumentTypeId { get; set; }

    /// <summary>
    /// Whether to reclassify documents that were manually confirmed
    /// (<see cref="DocumentReviewDisposition.Confirmed"/>). Default <c>false</c> protects manual
    /// confirmations (#289 default-on), making "overwrite human work" an explicit opt-in. Meaningless
    /// for <see cref="ReclassificationScope.PendingReviewQueue"/> because pending-review documents are
    /// not confirmed.
    /// </summary>
    public bool IncludeManuallyConfirmed { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Scope == ReclassificationScope.OnlyCurrentType && !DocumentTypeId.HasValue)
        {
            yield return new ValidationResult(
                "DocumentTypeId is required when Scope is OnlyCurrentType.",
                new[] { nameof(DocumentTypeId) });
        }
    }
}

/// <summary>
/// Bulk reprocessing trigger result: estimated number of documents enqueued this time, based on the
/// count-query snapshot at trigger time. The dispatcher chain enumeration is the final source of
/// truth, so this remains an estimate. Batches / progress are internal operations state and do not
/// enter the outbound contract.
/// </summary>
public class ReprocessingStartResultDto
{
    public long EstimatedDocumentCount { get; set; }
}

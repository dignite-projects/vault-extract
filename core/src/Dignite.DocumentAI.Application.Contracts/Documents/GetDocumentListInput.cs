using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dignite.DocumentAI.Documents;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Validation;

namespace Dignite.DocumentAI.Documents;

public class GetDocumentListInput : PagedAndSortedResultRequestDto
{
    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string? DocumentTypeCode { get; set; }

    /// <summary>Filters by manual-review <b>disposition stage</b> (#284), such as confirmed / rejected.</summary>
    public DocumentReviewDisposition? ReviewDisposition { get; set; }

    /// <summary>true returns only documents with any unresolved review reason (operator review queue, #284; includes unresolved classification + missing required fields in one queue).</summary>
    public bool? HasReviewReasons { get; set; }

    /// <summary>
    /// Soft-delete filter: null or false returns only non-deleted documents, the default behavior via
    /// EF DataFilter; true returns only soft-deleted documents (recycle-bin view), requiring
    /// <see cref="Documents.DocumentAIPermissions.Documents.Restore"/> permission.
    /// </summary>
    public bool? IsDeleted { get; set; }

    /// <summary>Filters by cabinet (#194). null means no filter; a concrete Guid returns only documents in that cabinet.</summary>
    public Guid? CabinetId { get; set; }

    /// <summary>
    /// Provenance filter (#354): when set, returns only the sub-documents derived from this source document
    /// (those whose <c>Document.OriginDocumentId</c> equals it) — the queryable form of the container /
    /// sub-document relationship surfaced by #350. null means no filter. Stays under the ABP <c>IMultiTenant</c>
    /// global filter, so it can only ever reach documents in the caller's own tenant.
    /// </summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>
    /// ExtractedFields value filters. Multiple filters are ANDed, and all are anchored to
    /// <see cref="DocumentTypeCode"/>. When provided, <see cref="DocumentTypeCode"/> is required
    /// because field declaration types must be resolved by type. Each element must carry Name plus at
    /// least one value, enforced by <see cref="DocumentFieldFilter"/> self-validation. Empty / null
    /// means metadata-only search.
    /// </summary>
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Forward base validation first. LimitedResultRequestDto validates
        // MaxResultCount <= MaxMaxResultCount. This method previously hid, rather than overrode, base
        // Validate, which silently disabled that limit validation (CS0114).
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        // Field-value filters must be anchored to a single type because fields such as amount have no
        // deterministic meaning outside a type and field types are resolved by type. Fail loudly via
        // AbpValidationException instead of silently ignoring this.
        if (FieldFilters is { Count: > 0 } && string.IsNullOrWhiteSpace(DocumentTypeCode))
        {
            yield return new ValidationResult(
                "DocumentTypeCode is required when field filters are specified.",
                new[] { nameof(DocumentTypeCode) });
        }
    }
}

using System;
using Dignite.Paperbase.Documents;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Contracts;

public class Contract : AuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual Guid DocumentId { get; private set; }

    public virtual string DocumentTypeCode { get; private set; } = default!;

    public virtual string? Title { get; private set; }

    public virtual string? ContractNumber { get; private set; }

    /// <summary>
    /// Comparison key for <see cref="ContractNumber"/> — derived via
    /// <see cref="DocumentIdentifierNormalization.NormalizeIdentifierCode"/>. Maintained in
    /// lockstep with <see cref="ContractNumber"/> through <see cref="ApplyFields"/> and
    /// <see cref="CorrectFields"/>. Indexed by EF Core; L2 RelationDiscovery looks up
    /// contracts via this column so casing / hyphenation / full-width-vs-half-width
    /// variants of the same business number actually match.
    /// </summary>
    public virtual string? NormalizedContractNumber { get; private set; }

    public virtual string? PartyAName { get; private set; }

    public virtual string? PartyBName { get; private set; }

    public virtual DateTime? SignedDate { get; private set; }

    public virtual DateTime? EffectiveDate { get; private set; }

    public virtual DateTime? ExpirationDate { get; private set; }

    public virtual decimal? TotalAmount { get; private set; }

    public virtual string? Currency { get; private set; }

    public virtual bool? AutoRenewal { get; private set; }

    public virtual int? TerminationNoticeDays { get; private set; }

    public virtual string? GoverningLaw { get; private set; }

    public virtual string? Summary { get; private set; }

    public virtual ContractStatus Status { get; private set; }

    public virtual double? ExtractionConfidence { get; private set; }

    public virtual bool NeedsReview { get; private set; }

    public virtual ContractReviewStatus ReviewStatus { get; private set; }

    protected Contract()
    {
    }

    internal Contract(
        Guid id,
        Guid? tenantId,
        Guid documentId,
        string documentTypeCode,
        ContractFields fields)
        : base(id)
    {
        TenantId = tenantId;
        DocumentId = documentId;
        DocumentTypeCode = documentTypeCode;
        Status = ContractStatus.Draft;

        UpdateExtractedFields(fields);
    }

    /// <summary>
    /// Replaces typed values from a fresh AI extraction. Always flips review state to
    /// <see cref="ContractReviewStatus.Pending"/> — a new extraction must be re-reviewed.
    /// </summary>
    public virtual void UpdateExtractedFields(ContractFields fields)
    {
        ApplyFields(fields);
        ExtractionConfidence = fields.ExtractionConfidence;
        SetReviewStatus(ContractReviewStatus.Pending);
    }

    public virtual void Confirm()
    {
        SetReviewStatus(ContractReviewStatus.Confirmed);
        Status = ContractStatus.Active;
    }

    /// <summary>
    /// Diffs <paramref name="fields"/> against current values. If anything changed,
    /// the corresponding typed properties are updated and the aggregate is flipped to
    /// <see cref="ContractReviewStatus.Corrected"/> with <c>ExtractionConfidence = 1.0</c>.
    /// If nothing changed, this is a no-op and review state is preserved.
    /// </summary>
    /// <returns><c>true</c> when at least one field changed; otherwise <c>false</c>.</returns>
    public virtual bool CorrectFields(ContractFields fields)
    {
        var changed = false;

        if (Title != fields.Title) { Title = fields.Title; changed = true; }
        if (ContractNumber != fields.ContractNumber)
        {
            ContractNumber = fields.ContractNumber;
            NormalizedContractNumber = NormalizeContractNumber(fields.ContractNumber);
            changed = true;
        }
        if (PartyAName != fields.PartyAName) { PartyAName = fields.PartyAName; changed = true; }
        if (PartyBName != fields.PartyBName) { PartyBName = fields.PartyBName; changed = true; }
        if (SignedDate != fields.SignedDate) { SignedDate = fields.SignedDate; changed = true; }
        if (EffectiveDate != fields.EffectiveDate) { EffectiveDate = fields.EffectiveDate; changed = true; }
        if (ExpirationDate != fields.ExpirationDate) { ExpirationDate = fields.ExpirationDate; changed = true; }
        if (TotalAmount != fields.TotalAmount) { TotalAmount = fields.TotalAmount; changed = true; }
        if (Currency != fields.Currency) { Currency = fields.Currency; changed = true; }
        if (AutoRenewal != fields.AutoRenewal) { AutoRenewal = fields.AutoRenewal; changed = true; }
        if (TerminationNoticeDays != fields.TerminationNoticeDays) { TerminationNoticeDays = fields.TerminationNoticeDays; changed = true; }
        if (GoverningLaw != fields.GoverningLaw) { GoverningLaw = fields.GoverningLaw; changed = true; }
        if (Summary != fields.Summary) { Summary = fields.Summary; changed = true; }

        if (!changed)
        {
            return false;
        }

        ExtractionConfidence = 1.0;
        SetReviewStatus(ContractReviewStatus.Corrected);
        return true;
    }

    public virtual void ArchiveBecauseDocumentDeleted()
    {
        Status = ContractStatus.Archived;
    }

    public virtual void RestoreBecauseDocumentRestored()
    {
        if (Status != ContractStatus.Archived)
        {
            return;
        }

        Status = ContractStatus.Draft;
        SetReviewStatus(ContractReviewStatus.Pending);
    }

    protected virtual void ApplyFields(ContractFields fields)
    {
        ValidateFields(fields);
        Title = fields.Title;
        ContractNumber = fields.ContractNumber;
        NormalizedContractNumber = NormalizeContractNumber(fields.ContractNumber);
        PartyAName = fields.PartyAName;
        PartyBName = fields.PartyBName;
        SignedDate = fields.SignedDate;
        EffectiveDate = fields.EffectiveDate;
        ExpirationDate = fields.ExpirationDate;
        TotalAmount = fields.TotalAmount;
        Currency = fields.Currency;
        AutoRenewal = fields.AutoRenewal;
        TerminationNoticeDays = fields.TerminationNoticeDays;
        GoverningLaw = fields.GoverningLaw;
        Summary = fields.Summary;
    }

    /// <summary>
    /// Last-line-of-defense domain guard. The LLM-side
    /// <c>ContractExtractionValidator</c> already enforces these rules at the raw
    /// JSON shape, but we re-check on the typed <see cref="ContractFields"/> just
    /// before persisting so any future bypass path (admin tool, data import, hand-rolled
    /// migration) cannot write inconsistent values into the aggregate.
    /// </summary>
    protected virtual void ValidateFields(ContractFields fields)
    {
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        if (fields.TotalAmount.HasValue && fields.TotalAmount.Value < 0)
        {
            throw new BusinessException("Contracts:InvalidContractField")
                .WithData("Field", nameof(fields.TotalAmount))
                .WithData("Value", fields.TotalAmount.Value);
        }

        if (fields.EffectiveDate.HasValue && fields.ExpirationDate.HasValue &&
            fields.EffectiveDate.Value > fields.ExpirationDate.Value)
        {
            throw new BusinessException("Contracts:InvalidContractField")
                .WithData("Field", nameof(fields.EffectiveDate))
                .WithData("EffectiveDate", fields.EffectiveDate.Value)
                .WithData("ExpirationDate", fields.ExpirationDate.Value);
        }

        if (fields.TerminationNoticeDays.HasValue && fields.TerminationNoticeDays.Value < 0)
        {
            throw new BusinessException("Contracts:InvalidContractField")
                .WithData("Field", nameof(fields.TerminationNoticeDays))
                .WithData("Value", fields.TerminationNoticeDays.Value);
        }
    }

    protected virtual void SetReviewStatus(ContractReviewStatus reviewStatus)
    {
        ReviewStatus = reviewStatus;
        NeedsReview = reviewStatus == ContractReviewStatus.Pending;
    }

    /// <summary>
    /// Centralized helper so <see cref="NormalizedContractNumber"/> is computed identically
    /// everywhere <see cref="ContractNumber"/> is written. Empty raw value → null normalized
    /// (consistent with how L2 RelationDiscovery treats empty normalized values as "no
    /// identifier" and skips them).
    /// </summary>
    protected static string? NormalizeContractNumber(string? rawContractNumber)
    {
        if (string.IsNullOrWhiteSpace(rawContractNumber)) return null;
        var normalized = DocumentIdentifierNormalization.NormalizeIdentifierCode(rawContractNumber);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

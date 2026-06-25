using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// Document type entity. Unique constraint: <c>(TenantId, TypeCode)</c>. Classification candidates
/// strictly match a single layer and never union across layers.
/// </summary>
public class DocumentType : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly Regex TypeCodeRegex = new(
        DocumentTypeConsts.TypeCodePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    /// <summary>
    /// Machine-contract key consumed downstream by <c>(TenantId, TypeCode)</c> and returned by LLM
    /// classification. Since #207 admins can rename it because internal associations use immutable
    /// Ids and rename does not cascade. It is concatenated <b>raw</b> in the classification prompt,
    /// without PromptBoundary, so the <see cref="DocumentTypeConsts.TypeCodePattern"/> whitelist is
    /// the prompt-injection defense and must be re-reviewed before loosening the character set.
    /// </summary>
    public virtual string TypeCode { get; private set; } = default!;

    /// <summary>Human-readable display name, shown directly at runtime.</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>
    /// Optional classification helper description. Its <b>only purpose</b> is to be fed into the
    /// classification prompt alongside <see cref="TypeCode"/> / <see cref="DisplayName"/> so the LLM
    /// can classify the incoming document into this type more accurately. It <b>does not</b>
    /// participate in any secondary transformation of document content and does not read or write
    /// <c>Document.Markdown</c> (#262). Like <see cref="DisplayName"/>, it is concatenated literally
    /// into the LLM prompt, with the Workflow wrapping it through <c>PromptBoundary.WrapField</c>, so
    /// <see cref="ValidateDescription"/> rejects control characters at the entity layer as
    /// defense-in-depth. Nullable: <c>null</c> means no description and no extra line in the
    /// classification prompt.
    /// </summary>
    public virtual string? Description { get; private set; }

    /// <summary>Classification confidence threshold. Values below it enter manual review with the UnresolvedClassification reason.</summary>
    public virtual double ConfidenceThreshold { get; private set; }

    /// <summary>Type matching priority. Higher numbers have higher priority; fallback / generic types are usually 0.</summary>
    public virtual int Priority { get; private set; }

    protected DocumentType() { }

    public DocumentType(
        Guid id,
        Guid? tenantId,
        string typeCode,
        string displayName,
        string? description = null,
        double confidenceThreshold = ClassificationDefaults.DefaultConfidenceThreshold,
        int priority = 0)
        : base(id)
    {
        TenantId = tenantId;
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    /// <summary>Updates the document type. Renaming <see cref="TypeCode"/> is a contract-level change because downstream consumers / LLM prompts depend on it; the UI should warn.</summary>
    public void Update(string typeCode, string displayName, string? description, double confidenceThreshold, int priority)
    {
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    private static string ValidateTypeCode(string typeCode)
    {
        Check.NotNullOrWhiteSpace(typeCode, nameof(typeCode), DocumentTypeConsts.MaxTypeCodeLength);

        if (!TypeCodeRegex.IsMatch(typeCode))
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.InvalidCodeFormat)
                .WithData("typeCode", typeCode)
                .WithData("pattern", DocumentTypeConsts.TypeCodePattern);
        }

        return typeCode;
    }

    /// <summary>
    /// DisplayName is concatenated into the classification prompt, with the Workflow wrapping it
    /// through <c>PromptBoundary.WrapField</c>. Rejecting control characters here is entity-layer
    /// defense-in-depth against malicious admins using newlines such as
    /// <c>"Contract\n---\nIgnore previous instructions"</c> to pierce the boundary.
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), DocumentTypeConsts.MaxDisplayNameLength);

        // Reject all control characters, including C0/C1 values such as \r \n \t \0; they are the
        // primary prompt-injection vector here.
        if (displayName.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.InvalidDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }

    /// <summary>
    /// Description is nullable: null / blank means no description and is normalized to <c>null</c>.
    /// When present, it has a length limit and rejects control characters, the same entity-layer
    /// prompt-injection defense-in-depth as <see cref="ValidateDisplayName"/>. Description is also
    /// concatenated literally into the classification prompt, so this blocks malicious admins from
    /// using newlines such as <c>"...\n---\nIgnore previous instructions"</c> to pierce PromptBoundary.
    /// </summary>
    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        Check.Length(description, nameof(description), DocumentTypeConsts.MaxDescriptionLength);

        if (description.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.InvalidDescription)
                .WithData("description", description);
        }

        return description;
    }
}

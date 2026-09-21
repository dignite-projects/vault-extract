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

    /// <summary>
    /// What counts as a duplicate for this type (#651): the set a document's <see cref="Document.FieldFingerprint"/>
    /// is compared against by duplicate detection (#411). <see cref="DuplicateDetectionScope.Layer"/> — the whole
    /// layer + type, whoever uploaded — is the default and the pre-#651 behaviour;
    /// <see cref="DuplicateDetectionScope.Uploader"/> narrows a collision to documents sharing the subject's
    /// <see cref="Document.CreatorId"/>, for the tenant where each department legitimately keeps its own copy.
    /// <para>
    /// Read at <b>execution time</b> by every detection entry point, never captured into a job argument, so two
    /// switches in quick succession converge on the final setting instead of racing. Changing it enqueues
    /// <c>DuplicateScopeReconciliationJob</c>, because <see cref="DocumentReviewReasons.DuplicateSuspected"/> is a
    /// persisted bit rather than a computed view and both switch directions leave a stale half.
    /// </para>
    /// </summary>
    public virtual DuplicateDetectionScope DuplicateScope { get; private set; }

    protected DocumentType() { }

    /// <summary>
    /// <paramref name="duplicateScope"/> keeps a default here, unlike on <see cref="Update"/> (#651). The
    /// asymmetry is deliberate: a constructor picks a value for a row that does not exist yet, where
    /// <see cref="DuplicateDetectionScope.Layer"/> is the documented product default and the pre-#651 behaviour,
    /// whereas <see cref="Update"/> <b>overwrites a persisted one</b> — which is the hole the pack import fell
    /// into. Requiring it here would also have forced <paramref name="description"/> /
    /// <paramref name="confidenceThreshold"/> / <paramref name="priority"/> to lose their defaults (an optional
    /// parameter may not precede a required one), breaking every consumer that constructs a type by its four
    /// identifying values — a break unrelated to anything #651 is about.
    /// </summary>
    public DocumentType(
        Guid id,
        Guid? tenantId,
        string typeCode,
        string displayName,
        string? description = null,
        double confidenceThreshold = ClassificationDefaults.DefaultConfidenceThreshold,
        int priority = 0,
        DuplicateDetectionScope duplicateScope = DuplicateDetectionScope.Layer)
        : base(id)
    {
        TenantId = tenantId;
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
        DuplicateScope = ValidateDuplicateScope(duplicateScope);
    }

    /// <summary>
    /// Updates the document type. Renaming <see cref="TypeCode"/> is a contract-level change because downstream
    /// consumers / LLM prompts depend on it; the UI should warn.
    /// <para>
    /// <paramref name="duplicateScope"/> is required and deliberately un-defaulted (#651), which also restores
    /// this method's original all-required shape. A default here would not merely pick a value for a new row —
    /// it <b>overwrites a persisted one</b>, so a save path whose author never noticed the parameter resets
    /// every <see cref="DuplicateDetectionScope.Uploader"/> type back to
    /// <see cref="DuplicateDetectionScope.Layer"/> and silently changes what counts as a duplicate across that
    /// type's whole corpus. The pack import did exactly this until it was caught. The constructor keeps its
    /// default for the opposite reason; see its own remarks.
    /// </para>
    /// </summary>
    public void Update(
        string typeCode,
        string displayName,
        string? description,
        double confidenceThreshold,
        int priority,
        DuplicateDetectionScope duplicateScope)
    {
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
        DuplicateScope = ValidateDuplicateScope(duplicateScope);
    }

    /// <summary>
    /// The enum's integer values are persisted and serialized (#651), so an undefined member must not reach the
    /// column: a row holding one would be read back as a scope no detection path knows how to apply, and JSON
    /// deserialization happily turns an arbitrary integer into an enum. Guarded at the entity because the pack
    /// import writes this straight from a file. An <see cref="ArgumentException"/> rather than a
    /// <see cref="BusinessException"/> and a new frozen error code, for the same reason
    /// <see cref="ConfidenceThreshold"/>'s out-of-range guard is <c>Check.Range</c>: this is a programming /
    /// malformed-input error, not a business rule an operator can act on. The DTOs carry
    /// <c>[EnumDataType]</c> so a bad REST payload is answered as a 400 before it ever gets here.
    /// </summary>
    private static DuplicateDetectionScope ValidateDuplicateScope(DuplicateDetectionScope duplicateScope)
    {
        if (!Enum.IsDefined(duplicateScope))
        {
            throw new ArgumentException(
                $"Undefined {nameof(DuplicateDetectionScope)} value: {(int)duplicateScope}.",
                nameof(duplicateScope));
        }

        return duplicateScope;
    }

    private static string ValidateTypeCode(string typeCode)
    {
        Check.NotNullOrWhiteSpace(typeCode, nameof(typeCode), DocumentTypeConsts.MaxTypeCodeLength);

        if (!TypeCodeRegex.IsMatch(typeCode))
        {
            throw new BusinessException(VaultExtractErrorCodes.DocumentType.InvalidCodeFormat)
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
            throw new BusinessException(VaultExtractErrorCodes.DocumentType.InvalidDisplayName)
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
            throw new BusinessException(VaultExtractErrorCodes.DocumentType.InvalidDescription)
                .WithData("description", description);
        }

        return description;
    }
}

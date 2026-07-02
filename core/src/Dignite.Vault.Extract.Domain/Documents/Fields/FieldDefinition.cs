using System;
using System.Linq;
using System.Text.RegularExpressions;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Field definition entity. Unique constraint is <c>(TenantId, DocumentTypeId, Name)</c>; field extraction matches exactly one layer and never unions across layers.
/// </summary>
public class FieldDefinition : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly Regex NameRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    /// <summary>Internal association to parent document type: references <see cref="DocumentType"/>.Id (reference-by-id, no navigation; #207).</summary>
    public virtual Guid DocumentTypeId { get; private set; }

    /// <summary>
    /// Machine contract key: JSON schema key in LLM prompts, <c>ExtractedFields</c> dictionary key, and downstream contract ID.
    /// Since #207, admins may rename it because internal associations use immutable Id and rename does not cascade.
    /// Constrained by the <see cref="FieldDefinitionConsts.NamePattern"/> allow-list. It is inserted raw into prompt schema,
    /// so the allow-list is the injection boundary.
    /// </summary>
    public virtual string Name { get; private set; } = default!;

    /// <summary>Display name, human-readable and shown directly at runtime. <b>Does not enter LLM prompts</b>.</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>
    /// LLM extraction instruction: tells the model what value to find in the document. <b>Optional</b>: when left empty (null),
    /// the model infers what to extract from <see cref="Name"/> (machine name) + <see cref="DataType"/> only.
    /// It can be omitted when the field name is sufficiently self-explanatory, such as <c>contract_amount</c> / <c>issue_date</c>.
    /// </summary>
    public virtual string? Prompt { get; private set; }

    public virtual FieldDataType DataType { get; private set; }

    public virtual int DisplayOrder { get; private set; }

    public virtual bool IsRequired { get; private set; }

    /// <summary>
    /// Whether multi-value is allowed (#212). Valid only for <see cref="FieldDataType.Text"/> fields.
    /// When true, extracted values are stored as multiple <see cref="DocumentExtractedField"/> rows, with <c>Order</c> in the composite key,
    /// and exported <c>ExtractedFields</c> renders as a JSON array. LLM extraction schema tells the model to return <c>string[]</c>.
    /// Forcing multi-value on non-text types loud-fails in the entity layer (see <see cref="ValidateMultiValue"/>).
    /// </summary>
    public virtual bool AllowMultiple { get; private set; }

    /// <summary>
    /// Whether this field participates in the document type's <b>duplicate-detection unique key</b> (#411). The
    /// normalized values of all <see cref="IsUniqueKey"/> fields are hashed into
    /// <see cref="Documents.Document.FieldFingerprint"/> after extraction; two documents in the same layer + type
    /// sharing that fingerprint are flagged <see cref="DocumentReviewReasons.DuplicateSuspected"/> (a likely
    /// re-upload of the same business entity, e.g. an invoice number + date + amount). Independent of
    /// <see cref="IsRequired"/>: a unique-key field that has no extracted value makes the key partial, and a partial
    /// key is not fingerprinted (no false collisions). Any <see cref="FieldDataType"/> may participate.
    /// </summary>
    public virtual bool IsUniqueKey { get; private set; }

    protected FieldDefinition() { }

    public FieldDefinition(
        Guid id,
        Guid? tenantId,
        Guid documentTypeId,
        string name,
        string displayName,
        string? prompt,
        FieldDataType dataType,
        int displayOrder = 0,
        bool isRequired = false,
        bool allowMultiple = false,
        bool isUniqueKey = false)
        : base(id)
    {
        TenantId = tenantId;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = NormalizePrompt(prompt);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        AllowMultiple = ValidateMultiValue(allowMultiple, dataType);
        IsUniqueKey = isUniqueKey;
    }

    /// <summary>
    /// Updates a field definition. Renaming <see cref="Name"/> is a contract-level change because downstream consumers / LLM prompt schema depend on it;
    /// UI should warn about it. <paramref name="dataType"/> cannot change when extracted values already exist, preventing typed-column mismatch.
    /// Narrowing <paramref name="allowMultiple"/> from multi to single is also forbidden when values exist, preventing Order&gt;0 rows from becoming orphans.
    /// Both are asserted by AppService before calling this method.
    /// </summary>
    public void Update(string name, string displayName, string? prompt, FieldDataType dataType, int displayOrder, bool isRequired, bool allowMultiple, bool isUniqueKey)
    {
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = NormalizePrompt(prompt);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        AllowMultiple = ValidateMultiValue(allowMultiple, dataType);
        IsUniqueKey = isUniqueKey;
    }

    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), FieldDefinitionConsts.MaxNameLength);
        if (!NameRegex.IsMatch(name))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidName)
                .WithData("name", name)
                .WithData("pattern", FieldDefinitionConsts.NamePattern);
        }
        return name;
    }

    /// <summary>
    /// DisplayName does not enter LLM prompts; rejecting control characters here is defense-in-depth hygiene,
    /// protecting UI rendering / logs from line breaks, null bytes, and similar characters.
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), FieldDefinitionConsts.MaxDisplayNameLength);

        if (displayName.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }

    /// <summary>
    /// Normalizes a candidate display name into a **safe-to-save** form: replace control characters such as line breaks / tabs / null bytes with spaces,
    /// fold consecutive whitespace, and truncate to <see cref="FieldDefinitionConsts.MaxDisplayNameLength"/>.
    /// <para>
    /// Used by "draft from prompt" (#264) to prefill forms. It intentionally lives in the same class as <see cref="ValidateDisplayName"/>
    /// and shares the same control-character rejection domain: draft output normalized through this method is **guaranteed** to pass
    /// <see cref="ValidateDisplayName"/>, so saving will not immediately loud-fail.
    /// Display-name cleaning policy has a single truth source in the entity, avoiding a divergent sanitizer in the app layer.
    /// If the rejection domain tightens later, the two paths will not silently drift. Whitespace input -> empty string, letting callers treat it as "draft unavailable".
    /// </para>
    /// </summary>
    public static string NormalizeDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length > FieldDefinitionConsts.MaxDisplayNameLength)
        {
            cleaned = cleaned[..FieldDefinitionConsts.MaxDisplayNameLength];
            // Truncating by UTF-16 code units can split surrogate pairs and leave a trailing unpaired high surrogate.
            // ValidateDisplayName would allow it, but it is an invalid paired character and can break later JSON serialization / DB round-trips.
            // Drop the trailing unpaired high surrogate (#264 review2 #2).
            if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.Trim();
        }

        return cleaned;
    }

    /// <summary>
    /// Normalizes optional extraction instructions: blank values (null / whitespace-only) always collapse to null,
    /// meaning "no prompt"; when empty, the LLM infers from Name + DataType only.
    /// No length limit (#447): Prompt is admin-authored configuration (may be long, structured Markdown) and is
    /// persisted uncapped as <c>nvarchar(max)</c>. No NotNullOrWhiteSpace check because Prompt is optional.
    /// </summary>
    private static string? NormalizePrompt(string? prompt)
    {
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
    }

    /// <summary>
    /// Multi-value makes sense only for <see cref="FieldDataType.Text"/> (#212): multi-row storage with Order in the composite key
    /// has "short structured value list" semantics only for text, such as tags, keywords, or multiple parties.
    /// Number / Boolean / Date / DateTime multi-value has no realistic extraction scenario and would make typed-column query semantics ambiguous.
    /// Forcing multi-value on non-text types loud-fails.
    /// </summary>
    private static bool ValidateMultiValue(bool allowMultiple, FieldDataType dataType)
    {
        if (allowMultiple && dataType != FieldDataType.Text)
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.MultiValueRequiresStringType)
                .WithData("dataType", dataType.ToString());
        }

        return allowMultiple;
    }
}

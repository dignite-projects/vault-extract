using System;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Cabinet: the human organization ownership dimension (#194), orthogonal to
/// <see cref="DocumentTypes.DocumentType"/>. Cabinet answers "which group / batch does it belong to"
/// and is human-assigned; DocumentType answers "what is it" and is AI-classified. It uses a Guid
/// primary key plus layer-unique <see cref="Name"/> (<c>(TenantId, Name)</c>), and
/// <c>Document.CabinetId</c> references it with a nullable Guid.
/// <para>
/// Classification / field-extraction pipelines never read or write <c>CabinetId</c>. The only
/// exception is "AI fallback cabinet selection" when upload leaves it blank (#265): this feeds the
/// current layer cabinet <see cref="Name"/> + <see cref="Description"/> (#273), wrapped with
/// <c>PromptBoundary.WrapField</c>, to the LLM so it can choose one. This is an independent one-shot
/// upload-time step and does not make cabinets degrade into a second DocumentType; see
/// <c>CabinetSuggestionWorkflow</c>.
/// </para>
/// </summary>
public class Cabinet : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>Cabinet name, shown directly at runtime. Unique constraint <c>(TenantId, Name)</c>; names cannot repeat within a layer.</summary>
    public virtual string Name { get; private set; } = default!;

    /// <summary>
    /// Optional cabinet description (#273). Fed into the #265 cabinet-selection prompt alongside
    /// <see cref="Name"/> to help AI decide ownership; <c>null</c> means no description. It is also
    /// wrapped with <c>PromptBoundary.WrapField</c> before entering the LLM, so
    /// <see cref="ValidateDescription"/> rejects control characters as prompt-injection
    /// defense-in-depth, mirroring <c>DocumentType.Description</c>.
    /// </summary>
    public virtual string? Description { get; private set; }

    protected Cabinet() { }

    public Cabinet(Guid id, Guid? tenantId, string name, string? description = null)
        : base(id)
    {
        TenantId = tenantId;
        Name = ValidateName(name);
        Description = ValidateDescription(description);
    }

    public void Update(string name, string? description = null)
    {
        Name = ValidateName(name);
        Description = ValidateDescription(description);
    }

    /// <summary>Name hygiene validation: reject control characters. Name enters the #265 cabinet-selection prompt wrapped with WrapField, so this also acts as prompt-injection defense-in-depth.</summary>
    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), CabinetConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(ExtractErrorCodes.Cabinet.InvalidName)
                .WithData("name", name);
        }

        return name;
    }

    /// <summary>
    /// Description is nullable: null / blank is normalized to <c>null</c> and the cabinet-selection
    /// prompt does not add that line. When present, it has a length limit and rejects control
    /// characters, the same injection defense-in-depth as <see cref="ValidateName"/> and mirroring
    /// <c>DocumentType.ValidateDescription</c>.
    /// </summary>
    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        Check.Length(description, nameof(description), CabinetConsts.MaxDescriptionLength);

        if (description.Any(char.IsControl))
        {
            throw new BusinessException(ExtractErrorCodes.Cabinet.InvalidDescription)
                .WithData("description", description);
        }

        return description;
    }
}

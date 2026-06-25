using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export template aggregate root. Unique constraint: <c>(TenantId, Name)</c>. Same names across
/// layers are valid separate rows.
/// <para>
/// The export engine is the channel's "file outbound surface": it only performs field projection +
/// renaming + ordering + serialization, with <strong>zero business transformation</strong>. It does
/// not calculate tax, map accounts, or convert exchange rates. Tenants compose templates for business
/// formats; Extract ships no industry templates.
/// </para>
/// </summary>
public class ExportTemplate : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual string Name { get; private set; } = default!;

    public virtual ExportFormat Format { get; private set; }

    /// <summary>
    /// Document type this template applies to, referencing <see cref="DocumentType"/>.Id (#207). After
    /// export columns were narrowed to ExtractedField-only, templates are necessarily type-bound
    /// because columns reference field definitions under that type, so this association is
    /// <b>required</b>. Existence is validated by AppService, and hard delete of referenced types is
    /// blocked by FK RESTRICT.
    /// </summary>
    public virtual Guid DocumentTypeId { get; private set; }

    /// <summary>Column definitions ordered by Order ascending. Serialized as a whole into a large text column (degraded from native json after #206); no per-column query need, so no child table.</summary>
    public virtual IReadOnlyList<ExportColumn> Columns { get; private set; } = new List<ExportColumn>();

    protected ExportTemplate() { }

    public ExportTemplate(
        Guid id,
        Guid? tenantId,
        string name,
        ExportFormat format,
        Guid documentTypeId,
        IReadOnlyList<ExportColumn> columns)
        : base(id)
    {
        TenantId = tenantId;
        Name = ValidateName(name);
        Format = format;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        SetColumns(columns);
    }

    public void Update(
        string name,
        ExportFormat format,
        Guid documentTypeId,
        IReadOnlyList<ExportColumn> columns)
    {
        Name = ValidateName(name);
        Format = format;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        SetColumns(columns);
    }

    private void SetColumns(IReadOnlyList<ExportColumn> columns)
    {
        Check.NotNull(columns, nameof(columns));

        if (columns.Count == 0)
        {
            throw new BusinessException(ExtractErrorCodes.Export.TemplateRequiresColumn);
        }

        if (columns.Count > ExportTemplateConsts.MaxColumnCount)
        {
            throw new BusinessException(ExtractErrorCodes.Export.TemplateTooManyColumns)
                .WithData("count", columns.Count)
                .WithData("max", ExportTemplateConsts.MaxColumnCount);
        }

        var duplicate = columns
            .GroupBy(c => c.FieldDefinitionId)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new BusinessException(ExtractErrorCodes.Export.TemplateDuplicateField)
                .WithData("fieldDefinitionId", duplicate.Key);
        }

        Columns = columns.OrderBy(c => c.Order).ToList();
    }

    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), ExportTemplateConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(ExtractErrorCodes.Export.InvalidTemplateName)
                .WithData("name", name);
        }

        return name;
    }
}

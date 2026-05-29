using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Paperbase.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出模板聚合根。唯一约束 <c>(TenantId, Name)</c>；跨层同名是合法的两行。
/// <para>
/// 导出引擎是通道的"文件出口"——只做字段投影 + 重命名 + 排序 + 序列化，<strong>零业务转换</strong>
/// （不算税 / 不做科目映射 / 不做汇率换算）。业务格式靠租户拼模板组合，Paperbase 不预置行业模板。
/// </para>
/// </summary>
public class ExportTemplate : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual string Name { get; private set; } = default!;

    public virtual ExportFormat Format { get; private set; }

    /// <summary>
    /// 限定适用的文档类型（引用 <see cref="DocumentType"/>.Id，#207）。导出列收敛为 ExtractedField-only 后模板必然类型绑定
    /// （列引用该类型下的字段定义），故此关联<b>必填</b>。存在性由 AppService 校验，被引用类型硬删由 FK RESTRICT 阻止。
    /// </summary>
    public virtual Guid DocumentTypeId { get; private set; }

    /// <summary>列定义（按 Order 升序）。整体序列化进大文本列（#206 后由 native json 降级），无单列查询需求，不开子表。</summary>
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
            throw new BusinessException(PaperbaseErrorCodes.Export.TemplateRequiresColumn);
        }

        if (columns.Count > ExportTemplateConsts.MaxColumnCount)
        {
            throw new BusinessException(PaperbaseErrorCodes.Export.TemplateTooManyColumns)
                .WithData("count", columns.Count)
                .WithData("max", ExportTemplateConsts.MaxColumnCount);
        }

        var duplicate = columns
            .GroupBy(c => c.ColumnName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.Export.TemplateDuplicateColumnName)
                .WithData("columnName", duplicate.Key);
        }

        Columns = columns.OrderBy(c => c.Order).ToList();
    }

    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), ExportTemplateConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.Export.InvalidTemplateName)
                .WithData("name", name);
        }

        return name;
    }
}

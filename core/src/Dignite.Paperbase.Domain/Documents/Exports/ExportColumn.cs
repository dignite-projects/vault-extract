using System;
using System.Linq;
using Volo.Abp;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出模板的一列定义（值对象，#207 收敛为 ExtractedField-only）。每列引用一个类型绑定字段值
/// （按不可变 <see cref="FieldDefinitionId"/>），渲染为输出文件中名为 <see cref="ColumnName"/> 的列。
/// <para>
/// 系统通用字段（<c>SourceType</c> / <c>LifecycleStatus</c> / <c>ReviewStatus</c> / <c>Title</c>）由导出引擎
/// <b>固定输出</b>，不走模板列配置——它们是 Paperbase 稳定元数据契约，无需像业务字段一样配置（#207）。
/// </para>
/// <para>
/// 作为 <see cref="ExportTemplate.Columns"/> 整体序列化进大文本列——get-only 属性 + 唯一带参构造函数让
/// System.Text.Json 反序列化时复用同一构造（参数名匹配属性名），构造期校验在 DB round-trip 时复跑（DB 内数据本应合法）。
/// </para>
/// </summary>
public class ExportColumn
{
    /// <summary>
    /// 引用的类型绑定字段值（<c>FieldDefinition.Id</c>，#207）。AppService 保存时按 <c>(DocumentTypeId, fieldName)</c>
    /// 解析得到；输出 / 管理 UI 再 join 当前 <c>FieldDefinition.Name</c>。<c>FieldDefinition.Name</c> rename 不影响本引用。
    /// </summary>
    public Guid FieldDefinitionId { get; }

    /// <summary>输出文件中的列标题（人类可读，允许中日文，拒绝控制字符）。</summary>
    public string ColumnName { get; }

    /// <summary>列在输出中的排序（升序）。</summary>
    public int Order { get; }

    public ExportColumn(Guid fieldDefinitionId, string columnName, int order)
    {
        FieldDefinitionId = Check.NotDefaultOrNull<Guid>(fieldDefinitionId, nameof(fieldDefinitionId));
        ColumnName = ValidateColumnName(columnName);
        Order = order;
    }

    private static string ValidateColumnName(string columnName)
    {
        Check.NotNullOrWhiteSpace(columnName, nameof(columnName), ExportTemplateConsts.MaxColumnNameLength);

        if (columnName.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.Export.InvalidColumnName)
                .WithData("columnName", columnName);
        }

        return columnName;
    }
}

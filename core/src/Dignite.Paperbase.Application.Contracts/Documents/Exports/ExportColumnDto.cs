using System;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出模板的一列（#207 收敛为 ExtractedField-only）。系统字段（LifecycleStatus / ReviewStatus / Title）
/// 由导出引擎固定输出，不在此列出。
/// </summary>
public class ExportColumnDto
{
    /// <summary>引用的类型绑定字段定义不可变 Id（#207：内部稳定句柄，字段名可由 admin 重命名故不作引用键）。</summary>
    public Guid FieldDefinitionId { get; set; }

    /// <summary>输出文件中的列标题。</summary>
    public string ColumnName { get; set; } = default!;

    /// <summary>列在输出中的排序（升序）。</summary>
    public int Order { get; set; }
}

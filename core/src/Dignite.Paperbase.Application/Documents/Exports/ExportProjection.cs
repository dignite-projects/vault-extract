using System;
using System.Collections.Generic;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出查询投影——只取导出需要的字段，<strong>排除 Markdown</strong>（大 OCR/正文载荷）。
/// 投影到非实体类型时 EF 自动不 SELECT 未引用列、也不进 change tracker，避免为上万文档把 Markdown 拉进内存。
/// <para>
/// 固定系统字段（#207）：<see cref="SourceType"/> / <see cref="LifecycleStatus"/> / <see cref="ReviewStatus"/> /
/// <see cref="Title"/> 由导出引擎固定输出（不走模板列配置）。<see cref="ExtractedFields"/> 是
/// <see cref="DocumentExtractedField"/> child 行的 typed 投影（随文档一并 SELECT，相关子查询 / JOIN，非逐文档 N+1），
/// 模板列按 <see cref="ExtractedFieldProjection.FieldDefinitionId"/> 匹配。
/// </para>
/// </summary>
internal sealed class ExportProjection
{
    public SourceType SourceType { get; init; }
    public string? Title { get; init; }
    public DocumentLifecycleStatus LifecycleStatus { get; init; }
    public DocumentReviewStatus ReviewStatus { get; init; }
    public List<ExtractedFieldProjection> ExtractedFields { get; init; } = new();
}

/// <summary>单个类型绑定字段值的 typed 投影——导出按字段类型（来自 FieldDefinition.DataType，#208）渲染对应列的单元格字符串，按 <see cref="FieldDefinitionId"/> 匹配模板列（#207）。</summary>
internal sealed class ExtractedFieldProjection
{
    public Guid FieldDefinitionId { get; init; }
    public string? StringValue { get; init; }
    public bool? BooleanValue { get; init; }
    public decimal? DecimalValue { get; init; }
    public DateOnly? DateValue { get; init; }
    public DateTime? DateTimeValue { get; init; }
}

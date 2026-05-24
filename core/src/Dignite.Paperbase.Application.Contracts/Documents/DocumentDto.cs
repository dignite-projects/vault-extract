using System;
using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class DocumentDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string OriginalFileBlobName { get; set; } = default!;
    public SourceType SourceType { get; set; }
    public FileOriginDto FileOrigin { get; set; } = default!;

    /// <summary>所属文件柜（#194）。null = 未归类。柜名由前端用柜列表 map 显示。</summary>
    public Guid? CabinetId { get; set; }

    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewStatus ReviewStatus { get; set; }
    public double ClassificationConfidence { get; set; }
    public string? ClassificationReason { get; set; }

    /// <summary>
    /// 展示标题（文本提取流水线 Run 成功后写入）。
    /// 迁移之前的历史文档可能为 null，UI 需回退到 <see cref="FileOriginDto.OriginalFileName"/>。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 文档结构化 Markdown 内容（文本提取流水线 Run 成功后写入）。
    /// 前端可直接渲染；需要纯文本时由前端 strip 或后端通过 <c>MarkdownStripper.Strip</c> 投影。
    /// </summary>
    public string? Markdown { get; set; }

    /// <summary>
    /// 类型绑定字段抽取结果（字段架构 v2）。键 = FieldName（与 <see cref="FieldDefinitionDto.Name"/> 同形）。
    /// 来源层由 <see cref="TenantId"/> 决定（Host 文档 → Host 字段定义；租户文档 → 该租户字段定义）。
    /// 尚未抽取或无类型绑定字段时为 null。
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }

    public DateTime CreationTime { get; set; }
    public IList<DocumentPipelineRunDto> PipelineRuns { get; set; } = new List<DocumentPipelineRunDto>();
}

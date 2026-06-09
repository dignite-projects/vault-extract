using System;
using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class DocumentDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public FileOriginDto FileOrigin { get; set; } = default!;

    /// <summary>所属文件柜（#194）。null = 未归类。柜名由前端用柜列表 map 显示。</summary>
    public Guid? CabinetId { get; set; }

    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewDisposition ReviewDisposition { get; set; }

    /// <summary>待审原因集合（#284，<c>[Flags]</c>）。客户端直接渲染原因 badge，不自行推断。</summary>
    public DocumentReviewReasons ReviewReasons { get; set; }

    /// <summary>是否需要操作员关注（#284）= <c>ReviewReasons != None 且 ReviewDisposition != Rejected</c>（已拒绝文档保留客观原因但操作员已处置，不再算需关注）。服务端出口以免客户端推断。</summary>
    public bool RequiresReview { get; set; }

    /// <summary>待审原因结构化明细（#284）。详情厚/列表薄——仅单文档详情组装；无未解决原因时为 null。</summary>
    public List<ReviewReasonDetailDto>? ReviewReasonDetails { get; set; }

    /// <summary>操作员拒绝理由（#284，仅 <see cref="ReviewDisposition"/>=Rejected 时有值）。</summary>
    public string? RejectionReason { get; set; }

    public double ClassificationConfidence { get; set; }

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
    /// 文档语言（ISO 639-1 / IETF tag，文本提取阶段检测后写入）。未检测到时为 null。
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// 文本提取是否<b>完整</b>（#268）。<c>true</c> = 已捕获全部内容（默认 / 历史文档亦视为完整）；
    /// <c>false</c> = 已知有缺失（OCR 输出被截断 / 命中重复守卫被丢弃 / 多页 PDF 有页未能转写）。
    /// 下游消费方据此自行决定是否接收 / 降级 / 进人工复核——通道层不替下游拦截。
    /// 注意：这是<b>质量信号</b>，与内部 extraction provenance（provider 名 / 归档 BlobName，刻意不出口）不同。
    /// </summary>
    public bool ExtractionIsComplete { get; set; } = true;

    /// <summary>提取不完整时的简短诊断说明（<see cref="ExtractionIsComplete"/> 为 false 时）；完整时为 <c>null</c>。</summary>
    public string? ExtractionIncompleteReason { get; set; }

    /// <summary>
    /// 类型绑定字段抽取结果（字段架构 v2）。键 = FieldName（与 <see cref="FieldDefinitionDto.Name"/> 同形）。
    /// 来源层由 <see cref="TenantId"/> 决定（Host 文档 → Host 字段定义；租户文档 → 该租户字段定义）。
    /// 尚未抽取或无类型绑定字段时为 null。
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }

    public DateTime CreationTime { get; set; }

    // 运行记录 → IDocumentPipelineRunAppService.GetListAsync(documentId)（#216 拆为独立聚合根）。
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class DocumentListItemDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string OriginalFileBlobName { get; set; } = default!;
    public FileOriginDto FileOrigin { get; set; } = default!;

    /// <summary>所属文件柜（#194）。null = 未归类。柜名由前端用柜列表 map 显示。</summary>
    public Guid? CabinetId { get; set; }

    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewStatus ReviewStatus { get; set; }
    public double ClassificationConfidence { get; set; }

    /// <summary>
    /// 展示标题；迁移前的历史文档可能为 null，UI 需回退到 <see cref="FileOriginDto.OriginalFileName"/>。
    /// </summary>
    public string? Title { get; set; }

    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 软删除时间。仅当 <see cref="GetDocumentListInput.IsDeleted"/> = true（回收站视图）时有值。
    /// </summary>
    public DateTime? DeletionTime { get; set; }

    /// <summary>
    /// 该文档的类型绑定字段抽取结果（字段架构 v2），与 <see cref="DocumentDto.ExtractedFields"/> 同形。
    /// key = <see cref="FieldDefinition.Name"/>，value 为原样 <see cref="JsonElement"/>（保留声明类型）；未抽取时 null。
    /// 列表查询无条件带回——消费方（Angular 列表按 DocumentTypeCode 展示字段列 / MCP 出口）自行决定如何呈现。
    /// LLM-facing 出口（MCP）按 <see cref="JsonElement.ValueKind"/> 决定是否 PromptBoundary 包裹（传输层关注点，不在此通用 DTO 施加）。
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }
}

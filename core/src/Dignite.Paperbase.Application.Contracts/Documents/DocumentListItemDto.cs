using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class DocumentListItemDto : EntityDto<Guid>
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

    /// <summary>
    /// 展示标题；迁移前的历史文档可能为 null，UI 需回退到 <see cref="FileOriginDto.OriginalFileName"/>。
    /// </summary>
    public string? Title { get; set; }

    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 软删除时间。仅当 <see cref="GetDocumentListInput.IsDeleted"/> = true（回收站视图）时有值。
    /// </summary>
    public DateTime? DeletionTime { get; set; }
}

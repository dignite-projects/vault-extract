using System;
using Dignite.Paperbase.Documents;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class GetDocumentListInput : PagedAndSortedResultRequestDto
{
    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxDocumentTypeCodeLength))]
    public string? DocumentTypeCode { get; set; }

    public DocumentReviewStatus? ReviewStatus { get; set; }

    /// <summary>
    /// Keyword 子串搜索：命中 Title / 原始文件名 / Markdown 全文任一即返回。
    /// </summary>
    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxSearchKeywordLength))]
    public string? Keyword { get; set; }

    /// <summary>
    /// 软删除过滤：null 或 false = 仅返回未删除文档（默认行为，依赖 EF DataFilter）；
    /// true = 仅返回已软删除文档（回收站视图，需要 <see cref="Documents.PaperbasePermissions.Documents.Restore"/> 权限）。
    /// </summary>
    public bool? IsDeleted { get; set; }

    /// <summary>按文件柜筛选（#194）。null = 不筛选；具体 Guid = 仅返回该柜文档。</summary>
    public Guid? CabinetId { get; set; }
}

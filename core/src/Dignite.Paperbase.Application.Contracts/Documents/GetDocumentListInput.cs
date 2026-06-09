using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dignite.Paperbase.Documents;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class GetDocumentListInput : PagedAndSortedResultRequestDto
{
    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string? DocumentTypeCode { get; set; }

    /// <summary>按人工审核<b>处置阶段</b>过滤（#284；查"已确认 / 已拒绝"等）。</summary>
    public DocumentReviewDisposition? ReviewDisposition { get; set; }

    /// <summary>true = 仅返回有任一未解决待审原因的文档（操作员审核队列，#284；含分类未定 + 必填缺失，单队列）。</summary>
    public bool? HasReviewReasons { get; set; }

    /// <summary>
    /// 软删除过滤：null 或 false = 仅返回未删除文档（默认行为，依赖 EF DataFilter）；
    /// true = 仅返回已软删除文档（回收站视图，需要 <see cref="Documents.PaperbasePermissions.Documents.Restore"/> 权限）。
    /// </summary>
    public bool? IsDeleted { get; set; }

    /// <summary>按文件柜筛选（#194）。null = 不筛选；具体 Guid = 仅返回该柜文档。</summary>
    public Guid? CabinetId { get; set; }

    /// <summary>
    /// ExtractedFields 字段值过滤器（多个之间 AND，全部锚定 <see cref="DocumentTypeCode"/>）。
    /// 提供时 <see cref="DocumentTypeCode"/> 必填——字段声明类型需按类型解析。每个元素须带 Name + 至少一个值
    /// （由 <see cref="DocumentFieldFilter"/> 自校验）。空 / null = 仅按元数据检索。
    /// </summary>
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // 先转发基类校验——LimitedResultRequestDto 校验 MaxResultCount ≤ MaxMaxResultCount。
        // 此前本方法隐藏（而非 override）基类 Validate，导致该上限校验静默失效（CS0114）。
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        // 字段值过滤器必须锚定单一类型（amount 等字段离开类型无确定含义，且字段类型按类型解析）——
        // loud fail（AbpValidationException），不静默。
        if (FieldFilters is { Count: > 0 } && string.IsNullOrWhiteSpace(DocumentTypeCode))
        {
            yield return new ValidationResult(
                "DocumentTypeCode is required when field filters are specified.",
                new[] { nameof(DocumentTypeCode) });
        }
    }
}

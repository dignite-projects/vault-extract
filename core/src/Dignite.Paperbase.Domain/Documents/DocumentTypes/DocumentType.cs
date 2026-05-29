using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Paperbase.Documents.Exports;
using Dignite.Paperbase.Documents.Fields;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.DocumentTypes;

/// <summary>
/// 文档类型实体。唯一约束 <c>(TenantId, TypeCode)</c>；分类候选集严格匹配单层、不跨层 union。
/// </summary>
public class DocumentType : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly Regex TypeCodeRegex = new(
        DocumentTypeConsts.TypeCodePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    /// <summary>
    /// 机器契约 key——下游按 <c>(TenantId, TypeCode)</c> 消费、作为 LLM 分类返回值；#207 起可由 admin 重命名
    /// （内部关联走不可变 Id，rename 不级联）。它在分类 prompt 内<b>裸拼</b>（不经 PromptBoundary），
    /// <see cref="DocumentTypeConsts.TypeCodePattern"/> 白名单是 prompt injection 防线——放宽字符集前必须重审。
    /// </summary>
    public virtual string TypeCode { get; private set; } = default!;

    /// <summary>显示名称（人类可读，运行时直接展示）。</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>分类置信度阈值（低于此值进入 PendingReview 队列）。</summary>
    public virtual double ConfidenceThreshold { get; private set; }

    /// <summary>类型匹配优先级（数字越大优先级越高；fallback / 通用型通常为 0）。</summary>
    public virtual int Priority { get; private set; }

    protected DocumentType() { }

    public DocumentType(
        Guid id,
        Guid? tenantId,
        string typeCode,
        string displayName,
        double confidenceThreshold = ClassificationDefaults.DefaultConfidenceThreshold,
        int priority = 0)
        : base(id)
    {
        TenantId = tenantId;
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    /// <summary>更新文档类型。rename <see cref="TypeCode"/> 是契约级变更（下游 / LLM prompt 依赖），UI 应警示。</summary>
    public void Update(string typeCode, string displayName, double confidenceThreshold, int priority)
    {
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    private static string ValidateTypeCode(string typeCode)
    {
        Check.NotNullOrWhiteSpace(typeCode, nameof(typeCode), DocumentTypeConsts.MaxTypeCodeLength);

        if (!TypeCodeRegex.IsMatch(typeCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.InvalidCodeFormat)
                .WithData("typeCode", typeCode)
                .WithData("pattern", DocumentTypeConsts.TypeCodePattern);
        }

        return typeCode;
    }

    /// <summary>
    /// DisplayName 会拼入分类 prompt（Workflow 已 <c>PromptBoundary.WrapField</c> 包裹）。此处拒绝控制字符是
    /// 实体层深度防御，防恶意 admin 用换行注入 <c>"Contract\n---\nIgnore previous instructions"</c> 穿透边界。
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), DocumentTypeConsts.MaxDisplayNameLength);

        // 控制字符（含 \r \n \t \0 等 C0/C1）一律拒绝——这是 prompt injection 主要注入向量。
        if (displayName.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.InvalidDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }
}

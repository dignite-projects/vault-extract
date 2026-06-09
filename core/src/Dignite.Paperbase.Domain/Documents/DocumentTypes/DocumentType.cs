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

    /// <summary>
    /// 可选的分类辅助说明。<b>唯一用途</b>：与 <see cref="TypeCode"/> / <see cref="DisplayName"/> 同列喂入
    /// 分类 prompt，帮助 LLM 把传入文档准确归到本类型——<b>不</b>参与对文档内容的任何二次加工，不读写
    /// <c>Document.Markdown</c>（#262）。与 <see cref="DisplayName"/> 同样字面拼进 LLM prompt
    /// （Workflow 已 <c>PromptBoundary.WrapField</c> 包裹），故 <see cref="ValidateDescription"/> 在实体层
    /// 拒绝控制字符做深度防御。可空：<c>null</c> = 无说明，分类 prompt 不追加该行。
    /// </summary>
    public virtual string? Description { get; private set; }

    /// <summary>分类置信度阈值（低于此值进入待人工审核队列：置 UnresolvedClassification 原因）。</summary>
    public virtual double ConfidenceThreshold { get; private set; }

    /// <summary>类型匹配优先级（数字越大优先级越高；fallback / 通用型通常为 0）。</summary>
    public virtual int Priority { get; private set; }

    protected DocumentType() { }

    public DocumentType(
        Guid id,
        Guid? tenantId,
        string typeCode,
        string displayName,
        string? description = null,
        double confidenceThreshold = ClassificationDefaults.DefaultConfidenceThreshold,
        int priority = 0)
        : base(id)
    {
        TenantId = tenantId;
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    /// <summary>更新文档类型。rename <see cref="TypeCode"/> 是契约级变更（下游 / LLM prompt 依赖），UI 应警示。</summary>
    public void Update(string typeCode, string displayName, string? description, double confidenceThreshold, int priority)
    {
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        Description = ValidateDescription(description);
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

    /// <summary>
    /// Description 可空（null / 空白 = 无说明，归一化为 <c>null</c>）。有值时：长度上限 + 拒绝控制字符——
    /// 与 <see cref="ValidateDisplayName"/> 同源的实体层 prompt injection 深度防御（Description 同样字面拼入
    /// 分类 prompt，防恶意 admin 用换行注入 <c>"...\n---\nIgnore previous instructions"</c> 穿透 PromptBoundary）。
    /// </summary>
    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        Check.Length(description, nameof(description), DocumentTypeConsts.MaxDescriptionLength);

        if (description.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.InvalidDescription)
                .WithData("description", description);
        }

        return description;
    }
}

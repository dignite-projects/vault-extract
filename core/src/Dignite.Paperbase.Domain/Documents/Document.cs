using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

public class Document : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    // 多租户
    public virtual Guid? TenantId { get; private set; }

    /// <summary>BlobStore 中的 Key，写入后不可修改</summary>
    public virtual string OriginalFileBlobName { get; private set; } = default!;

    public virtual SourceType SourceType { get; private set; }

    /// <summary>文件来源信息（不可变）</summary>
    public virtual FileOrigin FileOrigin { get; private set; } = default!;

    /// <summary>
    /// 所属文件柜（人工组织归属维度，#194）。可空——null 表示"未归类"。
    /// 上传时由操作员人工设定，<b>正交于 pipeline</b>：OCR / 分类 / 字段抽取均不读不写此字段
    /// （否则柜子会退化成第二个 DocumentType，人工组织维度与 AI 内容维度焊死）。
    /// 以可空 Guid 外键引用 <see cref="Cabinet"/> 聚合根（DDD reference-by-id，无导航属性）。
    /// </summary>
    public virtual Guid? CabinetId { get; private set; }

    /// <summary>
    /// 文档类型标识（由分类流水线 Run 成功后写入）。
    /// null 表示当前没有已确认/可用的文档类型；是否等待人工确认由 <see cref="ReviewStatus"/> 表达。
    /// </summary>
    public virtual string? DocumentTypeCode { get; private set; }

    /// <summary>
    /// 文档宏观生命周期状态。
    /// 由 DocumentPipelineRunManager 根据关键流水线的 Run 结果派生，不由应用层直接设置。
    /// </summary>
    public virtual DocumentLifecycleStatus LifecycleStatus { get; private set; }

    /// <summary>
    /// 人工审核状态。
    /// 分类置信度不足或无法产出有效类型时自动置为 PendingReview；人工确认后置为 Reviewed；
    /// 新一轮自动分类成功时重置为 None。
    /// </summary>
    public virtual DocumentReviewStatus ReviewStatus { get; private set; }

    /// <summary>
    /// 提取的结构化 Markdown 内容（文本提取流水线 Run 成功后写入，不可变）。
    /// 这是 Document 唯一的文本载荷——下游需要纯文本时通过 <see cref="MarkdownStripper.Strip"/> 投影。
    /// </summary>
    public virtual string? Markdown { get; private set; }

    /// <summary>
    /// 文档展示标题（文本提取流水线 Run 成功后写入，不可变）。
    /// 由 <see cref="MarkdownTitleExtractor"/> 从 <see cref="Markdown"/> 提取，失败时上游回退为不带扩展名的文件名。
    /// 迁移之前的历史记录可能为 null；读路径需回退到 <see cref="FileOrigin"/>.OriginalFileName / <see cref="OriginalFileBlobName"/>。
    /// </summary>
    public virtual string? Title { get; private set; }

    /// <summary>
    /// 文档分类置信度（0.0 ~ 1.0），为最后一次成功分类 Run 的快照。
    /// 当 <see cref="DocumentTypeCode"/> 为 null 时此值为 0；是否等待人工确认由 <see cref="ReviewStatus"/> 表达。
    /// 人工确认（<see cref="DocumentReviewStatus.Reviewed"/>）时固定写入 1.0。
    /// </summary>
    public virtual double ClassificationConfidence { get; private set; }

    /// <summary>
    /// AI 对当前分类决策的业务解释（分类理由）。
    /// 仅在 <see cref="RequestClassificationReview"/> 路径（置信度不足或无法分类）时写入；
    /// 高置信度 <see cref="ApplyAutomaticClassificationResult"/> 时为 null；
    /// 人工确认（<see cref="ConfirmClassification"/>）后清空。
    /// 与 <see cref="DocumentPipelineRun.StatusMessage"/>（流水线技术错误信息）语义不同。
    /// </summary>
    public virtual string? ClassificationReason { get; private set; }

    // === 字段架构 v2：系统通用字段（顶层 typed columns，由 pipeline 各阶段填充） ===

    /// <summary>文档语言（ISO 639-1 / IETF tag）。OCR / 抽取阶段检测；影响下游 prompt 语言选择。</summary>
    public virtual string? Language { get; private set; }

    /// <summary>OCR 平均置信度（0..1）。OCR 完成后填充；CLAUDE.md "OCR 置信度门槛"依赖此值决定 <c>DocumentReadyEto</c> 是否发布。</summary>
    public virtual double? OcrConfidence { get; private set; }

    /// <summary>
    /// 类型绑定字段抽取结果（字段架构 v2）。键 = FieldName（与 LLM 输出 JSON 键同形）。
    /// <para>
    /// 来源由 <see cref="TenantId"/> 决定：
    /// <list type="bullet">
    ///   <item><c>TenantId IS NULL</c>（Host 文档）→ <c>FieldDefinition.TenantId IS NULL</c> 的字段定义</item>
    ///   <item><c>TenantId != null</c>（租户文档）→ <c>FieldDefinition.TenantId = Document.TenantId</c> 的字段定义</item>
    /// </list>
    /// 两层 mutually exclusive——同一 Document 只跑一层字段抽取，不存在分桶 / 命名冲突。
    /// </para>
    /// EF Core 10 SQL Server provider 不直接支持 <c>Dictionary&lt;string, JsonElement&gt;</c> ↔ <c>json</c> 列映射，
    /// 必须 ValueConverter 中转；底层 storage 仍为 SQL Server 2025 原生 <c>json</c> 列。
    /// </summary>
    public virtual Dictionary<string, JsonElement>? ExtractedFields { get; private set; }

    // --- 聚合内的 PipelineRun 集合 ---

    private readonly List<DocumentPipelineRun> _pipelineRuns = new();
    public virtual IReadOnlyCollection<DocumentPipelineRun> PipelineRuns => _pipelineRuns.AsReadOnly();

    // --- 派生访问器 ---

    /// <summary>根据 PipelineCode 查询最近一次 Run（按 AttemptNumber 降序）。</summary>
    public DocumentPipelineRun? GetLatestRun(string pipelineCode)
        => PipelineRuns
            .Where(r => r.PipelineCode == pipelineCode)
            .OrderByDescending(r => r.AttemptNumber)
            .FirstOrDefault();

    public DocumentPipelineRun? GetRun(Guid runId)
        => PipelineRuns.FirstOrDefault(r => r.Id == runId);

    protected Document()
    {
    }

    public Document(
        Guid id,
        Guid? tenantId,
        string originalFileBlobName,
        SourceType sourceType,
        FileOrigin fileOrigin,
        Guid? cabinetId = null)
        : base(id)
    {
        TenantId = tenantId;
        OriginalFileBlobName = Check.NotNullOrWhiteSpace(originalFileBlobName, nameof(originalFileBlobName));
        SourceType = sourceType;
        FileOrigin = Check.NotNull(fileOrigin, nameof(fileOrigin));
        CabinetId = cabinetId;
        LifecycleStatus = DocumentLifecycleStatus.Uploaded;
    }

    // --- 写入方法（由 DocumentPipelineRunManager 在流水线完成后调用） ---

    internal void SetMarkdown(string markdown)
    {
        if (!string.IsNullOrEmpty(Markdown))
            throw new BusinessException(PaperbaseErrorCodes.MarkdownIsImmutable);
        Markdown = string.IsNullOrEmpty(markdown) ? null : markdown;
    }

    internal void SetTitle(string? title)
    {
        if (!string.IsNullOrEmpty(Title))
            throw new BusinessException(PaperbaseErrorCodes.TitleIsImmutable);

        if (string.IsNullOrWhiteSpace(title))
        {
            Title = null;
            return;
        }

        var trimmed = title.Trim();
        Title = trimmed.Length <= DocumentConsts.MaxTitleLength
            ? trimmed
            : trimmed[..DocumentConsts.MaxTitleLength];
    }

    internal void SetSourceType(SourceType sourceType)
    {
        SourceType = sourceType;
    }

    /// <summary>
    /// 删柜时把文档回退"未归类"（#194）。CabinetId 是正交组织维度——清空它不触发任何 pipeline / 领域事件，
    /// 是原子状态变更（与 <see cref="SetExtractedFields"/> 同类，由 Application 层直接调，无需经 DomainService 中转）。
    /// 由 <c>CabinetAppService.DeleteAsync</c> 在删柜前对该柜全部文档调用，避免悬空指向已删柜。
    /// </summary>
    public void UnassignCabinet()
    {
        CabinetId = null;
    }

    internal void SetOcrConfidence(double? confidence)
    {
        OcrConfidence = confidence.HasValue
            ? Check.Range(confidence.Value, nameof(confidence), 0d, 1d)
            : null;
    }

    /// <summary>
    /// 写入字段抽取结果到 <see cref="ExtractedFields"/>。
    /// <see cref="FieldExtractionEventHandler"/> 在分类完成后调用；重分类时可覆写；null 或空字典清空。
    /// 原子状态变更，无需经 DomainService 中转（与 <see cref="SetMarkdown"/> 等
    /// 必须与 pipeline 完成事务组合调用的 internal setter 不同）。
    /// </summary>
    public void SetExtractedFields(IReadOnlyDictionary<string, JsonElement>? fields)
    {
        ExtractedFields = fields == null || fields.Count == 0
            ? null
            : new Dictionary<string, JsonElement>(fields);
    }

    // 高置信度路径：ClassificationReason 必须为 null，与 RequestClassificationReview 路径区分。
    internal void ApplyAutomaticClassificationResult(
        string documentTypeCode,
        double classificationConfidence)
    {
        DocumentTypeCode = Check.NotNullOrWhiteSpace(documentTypeCode, nameof(documentTypeCode));
        ClassificationConfidence = Check.Range(classificationConfidence, nameof(classificationConfidence), 0d, 1d);
        ClassificationReason = null;
        ReviewStatus = DocumentReviewStatus.None;
    }

    /// <summary>
    /// 标记为待人工审核：清空尚未确认的分类结果，避免历史值污染外部读模型。
    /// </summary>
    internal void RequestClassificationReview(string? reason = null)
    {
        DocumentTypeCode = null;
        ClassificationConfidence = 0;
        ClassificationReason = reason;
        ReviewStatus = DocumentReviewStatus.PendingReview;
    }

    internal void ConfirmClassification(string documentTypeCode)
    {
        DocumentTypeCode = Check.NotNullOrWhiteSpace(documentTypeCode, nameof(documentTypeCode));
        ClassificationConfidence = 1.0;
        ReviewStatus = DocumentReviewStatus.Reviewed;
        ClassificationReason = null;
    }

    /// <summary>
    /// OCR 置信度低于门槛时调用——文档进待人工审核队列，下游流水线暂停推进。
    /// ClassificationReason 复用为通用 review 原因字段（OCR 不达标 / LLM 分类失败均可写入）。
    /// <para>
    /// 原子状态变更，无需经 DomainService 中转（与 <see cref="SetMarkdown"/> 等
    /// 必须与 pipeline 完成事务组合调用的 internal setter 不同）。
    /// </para>
    /// </summary>
    public void MarkPendingOcrReview(string reason)
    {
        ReviewStatus = DocumentReviewStatus.PendingReview;
        ClassificationReason = reason;
    }

    /// <summary>
    /// 操作员通过审核——清除 PendingReview 标记。下游 pipeline 推进 / lifecycle 重新派生
    /// 由 AppService 编排（参见 <see cref="DocumentPipelineRunManager.RecomputeLifecycleAsync"/>）。
    /// <para>
    /// 原子状态变更，无需经 DomainService 中转（与 <see cref="SetMarkdown"/> 等
    /// 必须与 pipeline 完成事务组合调用的 internal setter 不同）。
    /// </para>
    /// </summary>
    public void ApproveReview()
    {
        ReviewStatus = DocumentReviewStatus.Reviewed;
        ClassificationReason = null;
    }

    /// <summary>
    /// 操作员拒绝审核——文档落到 Failed 生命周期状态；ReviewStatus 保留以便审计。
    /// OCR 不可用是当前数字化结果的终态结论；保留原文件、Markdown、OCR confidence 和拒绝原因，
    /// 不在同一 Document 上做源文件替换或普通 OCR 重跑。
    /// <para>
    /// <b>lifecycle 派生规则的合法例外</b>：通常 <see cref="LifecycleStatus"/> 由
    /// <see cref="DocumentPipelineRunManager"/> 从 pipeline run 状态派生（见类首注释 line 32-35），
    /// 此处直接 <see cref="TransitionLifecycle"/> 到 Failed 是 review 终态语义的合法越权：
    /// 拒绝即终态，无需等待 pipeline run 推进。
    /// </para>
    /// </summary>
    public void RejectReview(string? reason = null)
    {
        ClassificationReason = reason ?? ClassificationReason;
        TransitionLifecycle(DocumentLifecycleStatus.Failed);
    }

    internal void TransitionLifecycle(DocumentLifecycleStatus newStatus)
    {
        if (LifecycleStatus == newStatus)
            return;

        var oldStatus = LifecycleStatus;
        LifecycleStatus = newStatus;
        AddLocalEvent(new DocumentLifecycleStatusChangedEvent(Id, oldStatus, newStatus));
    }

    // --- 内部 PipelineRun 集合管理（仅 DocumentPipelineRunManager 可访问） ---

    internal void AddPipelineRun(DocumentPipelineRun run)
    {
        _pipelineRuns.Add(run);
    }

    internal void PublishPipelineRunCompletedEvent(DocumentPipelineRunCompletedEvent evt)
    {
        AddLocalEvent(evt);
    }
}

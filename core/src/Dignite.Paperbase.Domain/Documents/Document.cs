using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Cabinets;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Pipelines;
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
    /// 文档类型内部关联（由分类流水线 Run 成功后写入）。引用 <see cref="DocumentType"/>.Id（DDD reference-by-id，无导航属性）。
    /// null 表示当前没有已确认/可用的文档类型；是否等待人工确认由 <see cref="ReviewStatus"/> 表达。
    /// <para>
    /// 内部用不可变 Id 关联（#207）——外部 wire-format（REST / MCP / ETO）仍输出 <c>DocumentTypeCode</c> 字符串，
    /// 由读路径 join <see cref="DocumentType"/> 解析当前（或软删后最后已知）TypeCode。TypeCode rename 不再级联本表。
    /// </para>
    /// </summary>
    public virtual Guid? DocumentTypeId { get; private set; }

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

    // --- 聚合内的字段值集合（字段架构 v2 / Issue #206） ---

    private readonly List<DocumentExtractedField> _extractedFieldValues = new();

    /// <summary>
    /// 类型绑定字段抽取结果（字段架构 v2）——一行一个字段值的 child 集合，字段值查询与持久化的唯一 truth source。
    /// <para>
    /// 同一 Document 只跑一层字段抽取（来源层由 <see cref="TenantId"/> 决定）——不分桶、不存在跨层命名冲突。
    /// </para>
    /// 出口 DTO / MCP / REST 的 <c>ExtractedFields</c> 字典由 App / Mapper 层从这些行即时组装
    /// （见 <see cref="DocumentExtractedField.ToJsonElement"/>），wire-format 与旧 JSON 列兼容。
    /// </summary>
    public virtual IReadOnlyCollection<DocumentExtractedField> ExtractedFieldValues => _extractedFieldValues.AsReadOnly();

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
            throw new BusinessException(PaperbaseErrorCodes.Document.MarkdownIsImmutable);
        Markdown = string.IsNullOrEmpty(markdown) ? null : markdown;
    }

    internal void SetTitle(string? title)
    {
        if (!string.IsNullOrEmpty(Title))
            throw new BusinessException(PaperbaseErrorCodes.Document.TitleIsImmutable);

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
    /// 是原子状态变更（与 <see cref="SetFields"/> 同类，由 Application 层直接调，无需经 DomainService 中转）。
    /// 由 <c>CabinetAppService.DeleteAsync</c> 在删柜前对该柜全部文档调用，避免悬空指向已删柜。
    /// </summary>
    public void UnassignCabinet()
    {
        CabinetId = null;
    }

    /// <summary>
    /// 整组替换类型绑定字段值（字段架构 v2 / Issue #206 + #207）。<c>FieldExtractionEventHandler</c> 在分类完成后调用，
    /// 操作员手改（<c>UpdateExtractedFieldsAsync</c>）亦走此路径；传空集合清空全部字段行。
    /// 调用方提交该文档当前的全部字段值（已校验值类型与 <see cref="DocumentFieldValue.DataType"/> 对齐，
    /// 且每个 <see cref="DocumentFieldValue.FieldDefinitionId"/> 解析自该文档所属层 / 类型下的 <c>FieldDefinition</c>）。
    /// <para>
    /// 用 <b>reconcile</b> 而非 clear+add：同字段（按 <see cref="DocumentFieldValue.FieldDefinitionId"/>）<b>原地更新</b>、
    /// 消失的删除、新增的插入。原因——复合主键 <c>(DocumentId, FieldDefinitionId)</c> 下，clear+add 会在单次 SaveChanges 内
    /// 对同键产生 delete+insert，触发唯一冲突 / EF 操作排序风险（操作员把 <c>amount=100</c> 改 <c>200</c> 即同字段替换）。
    /// </para>
    /// 原子状态变更，无需经 DomainService 中转（与 <see cref="SetMarkdown"/> 等必须与 pipeline 完成事务组合的 internal setter 不同）。
    /// <b>前置条件</b>：<see cref="DocumentTypeId"/> 非空（字段挂在文档类型下；两条调用路径均在分类完成后调用）。
    /// </summary>
    public void SetFields(IEnumerable<DocumentFieldValue>? values)
    {
        var incoming = values?.ToList() ?? new List<DocumentFieldValue>();

        _extractedFieldValues.RemoveAll(existing =>
            incoming.All(v => v.FieldDefinitionId != existing.FieldDefinitionId));

        foreach (var value in incoming)
        {
            var existing = _extractedFieldValues.FirstOrDefault(
                f => f.FieldDefinitionId == value.FieldDefinitionId);
            if (existing != null)
            {
                existing.SetValue(value);
            }
            else
            {
                _extractedFieldValues.Add(new DocumentExtractedField(Id, TenantId, value));
            }
        }
    }

    // 高置信度路径：ClassificationReason 必须为 null，与 RequestClassificationReview 路径区分。
    internal void ApplyAutomaticClassificationResult(
        Guid documentTypeId,
        double classificationConfidence)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = Check.Range(classificationConfidence, nameof(classificationConfidence), 0d, 1d);
        ClassificationReason = null;
        ReviewStatus = DocumentReviewStatus.None;
    }

    /// <summary>
    /// 标记为待人工审核：清空尚未确认的分类结果，避免历史值污染外部读模型。
    /// </summary>
    internal void RequestClassificationReview(string? reason = null)
    {
        DocumentTypeId = null;
        ClassificationConfidence = 0;
        ClassificationReason = reason;
        ReviewStatus = DocumentReviewStatus.PendingReview;
    }

    internal void ConfirmClassification(Guid documentTypeId)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = 1.0;
        ReviewStatus = DocumentReviewStatus.Reviewed;
        ClassificationReason = null;
    }

    /// <summary>
    /// 操作员拒绝审核——文档落到 Failed 生命周期状态；ReviewStatus 保留以便审计。
    /// 拒绝是"当前数字化结果不可用 / 无法归类"的终态结论：保留原文件、Markdown、OCR confidence 和拒绝原因，
    /// 不在同一 Document 上做源文件替换或重跑；需要重试由操作员重新上传。
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

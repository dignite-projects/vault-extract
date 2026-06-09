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
    /// null 表示当前没有已确认/可用的文档类型；是否等待人工确认由 <see cref="ReviewDisposition"/> / <see cref="ReviewReasons"/> 表达。
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
    /// 人工审核<b>处置阶段</b>（操作员动作轴，#284）。NotReviewed（默认）/ Confirmed（人工确认类型）/
    /// Rejected（操作员拒绝，可恢复——后续 Reclassify 会转回 Confirmed）。
    /// 与<b>待审原因</b>（<see cref="ReviewReasons"/>）正交：本字段只由操作员动作写。
    /// "是否需操作员关注"由 <see cref="ReviewReasonPolicy.RequiresAttention(DocumentReviewReasons, DocumentReviewDisposition)"/>
    /// 派生（<c>ReviewReasons != None 且本字段 != Rejected</c>）——Rejected 抑制需关注（操作员已处置），其余仅由原因轴驱动。
    /// </summary>
    public virtual DocumentReviewDisposition ReviewDisposition { get; private set; }

    /// <summary>
    /// 待审原因集合（客观未解决问题轴，#284）——为什么需要操作员关注。每个 bit 由唯一一个 pipeline 阶段维护
    /// （UnresolvedClassification←分类阶段、MissingRequiredFields←字段抽取阶段，按位 set/clear 互不覆盖）。
    /// blocking 原因（见 <see cref="ReviewReasonPolicy"/>）经
    /// <see cref="Pipelines.DocumentPipelineRunManager.DeriveLifecycleAsync"/> 挡住 Ready；
    /// non-blocking 只进操作员队列、不阻断下游。
    /// </summary>
    public virtual DocumentReviewReasons ReviewReasons { get; private set; }

    /// <summary>
    /// 提取的结构化 Markdown 内容（文本提取流水线 Run 成功后写入，不可变）。
    /// 这是 Document 唯一的文本载荷——下游需要纯文本时通过 <see cref="MarkdownStripper.Strip"/> 投影。
    /// </summary>
    public virtual string? Markdown { get; private set; }

    /// <summary>
    /// 文档展示标题（文本提取流水线 Run 成功后写入，不可变）。
    /// 由 <see cref="MarkdownTitleExtractor"/> 从 <see cref="Markdown"/> 提取，失败时上游回退为不带扩展名的文件名。
    /// 迁移之前的历史记录可能为 null；读路径需回退到 <see cref="FileOrigin"/>.OriginalFileName / <see cref="FileOrigin"/>.BlobName。
    /// </summary>
    public virtual string? Title { get; private set; }

    /// <summary>
    /// 文档分类置信度（0.0 ~ 1.0），为最后一次成功分类 Run 的快照。
    /// 当 <see cref="DocumentTypeCode"/> 为 null 时此值为 0；是否等待人工确认由 <see cref="ReviewDisposition"/> / <see cref="ReviewReasons"/> 表达。
    /// 人工确认（<see cref="DocumentReviewDisposition.Confirmed"/>）时固定写入 1.0。
    /// </summary>
    public virtual double ClassificationConfidence { get; private set; }

    /// <summary>
    /// 操作员拒绝审核时填写的拒绝理由（#284：从原 ClassificationReason 独立出来，拒绝时<b>必填</b>）。
    /// 仅在 <see cref="ReviewDisposition"/> = Rejected 时有值；语义单一——人工输入的拒绝说明，
    /// 不再兼做 AI 分类解释。长度受 <see cref="DocumentConsts.MaxRejectionReasonLength"/> 约束。
    /// </summary>
    public virtual string? RejectionReason { get; private set; }

    // === 字段架构 v2：系统通用字段（顶层 typed columns，由 pipeline 各阶段填充） ===

    /// <summary>文档语言（ISO 639-1 / IETF tag）。OCR / 抽取阶段检测；影响下游 prompt 语言选择。</summary>
    public virtual string? Language { get; private set; }

    /// <summary>
    /// 文本提取 provenance 元数据（#210）：胜出 provider 名 + 原生 payload 归档清单。
    /// Domain 自有 typed 值对象 → JSON 列（解耦 provider 契约）。
    /// 原始 bbox / cell 等空间信号<b>留 blob</b>，本字段只存 manifest。文本提取流水线 Run 成功后写入；历史记录可能为 null。
    /// </summary>
    public virtual DocumentTextExtractionMetadata? ExtractionMetadata { get; private set; }

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

    protected Document()
    {
    }

    public Document(
        Guid id,
        Guid? tenantId,
        FileOrigin fileOrigin,
        Guid? cabinetId = null)
        : base(id)
    {
        TenantId = tenantId;
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

    /// <summary>
    /// 写入 OCR / 抽取阶段检测到的语言（#210：终结此前 write-never 死字段）。空 / 空白入参<b>不</b>覆盖
    /// （检测不到语言时保留既有值），超长截断到 <see cref="DocumentConsts.MaxLanguageLength"/>。
    /// 由 <see cref="Pipelines.DocumentPipelineRunManager.CompleteTextExtractionAsync"/> 在文本提取完成时调用。
    /// </summary>
    internal void SetLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        var trimmed = language.Trim();
        Language = trimmed.Length <= DocumentConsts.MaxLanguageLength
            ? trimmed
            : trimmed[..DocumentConsts.MaxLanguageLength];
    }

    /// <summary>
    /// 写入文本提取 provenance（#210）：Domain typed 元数据值对象（provider 名 + 归档 manifest）。
    /// 由 <see cref="Pipelines.DocumentPipelineRunManager.CompleteTextExtractionAsync"/> 调用，
    /// 与 <see cref="SetMarkdown"/> 同事务原子写入（Markdown write-once 不变式天然把本写入也钳成 write-once）。
    /// </summary>
    internal void SetExtractionMetadata(DocumentTextExtractionMetadata? extractionMetadata)
    {
        ExtractionMetadata = extractionMetadata;
    }

    /// <summary>
    /// 改派 / 指派文档所属文件柜（#257）。<c>null</c> = 移出文件柜（未归类）。
    /// CabinetId 是正交组织维度——改派<b>不</b>触发任何 pipeline / 领域事件，是原子状态变更
    /// （与 <see cref="SetFields"/> 同类，由 Application 层直接调，无需经 DomainService 中转）。
    /// 目标柜的存在性与当前层归属由 Application 层校验（<c>DocumentAppService.UpdateCabinetAsync</c>）。
    /// </summary>
    public void SetCabinet(Guid? cabinetId)
    {
        CabinetId = cabinetId;
    }

    /// <summary>
    /// 删柜时把文档回退"未归类"（#194）——<see cref="SetCabinet"/> 传 <c>null</c> 的语义别名。
    /// 由 <c>CabinetAppService.DeleteAsync</c> 在删柜前对该柜全部文档调用，避免悬空指向已删柜。
    /// </summary>
    public void UnassignCabinet()
    {
        SetCabinet(null);
    }

    /// <summary>
    /// 整组替换类型绑定字段值（字段架构 v2 / Issue #206 + #207）。<c>FieldExtractionEventHandler</c> 在分类完成后调用，
    /// 操作员手改（<c>UpdateExtractedFieldsAsync</c>）亦走此路径；传空集合清空全部字段行。
    /// 调用方提交该文档当前的全部字段值（已校验值类型与 <see cref="DocumentFieldValue.DataType"/> 对齐，
    /// 且每个 <see cref="DocumentFieldValue.FieldDefinitionId"/> 解析自该文档所属层 / 类型下的 <c>FieldDefinition</c>）。
    /// <para>
    /// 用 <b>reconcile</b> 而非 clear+add：同字段值行（按 <see cref="DocumentFieldValue.FieldDefinitionId"/> +
    /// <see cref="DocumentFieldValue.Order"/>，#212）<b>原地更新</b>、消失的删除、新增的插入。原因——复合主键
    /// <c>(DocumentId, FieldDefinitionId, Order)</c> 下，clear+add 会在单次 SaveChanges 内对同键产生 delete+insert，
    /// 触发唯一冲突 / EF 操作排序风险（操作员把 <c>amount=100</c> 改 <c>200</c> 即同字段同 Order 替换；多值文本
    /// 字段 <c>["a","b","c"] → ["x","y"]</c> 时 Order 0/1 原地改、Order 2 删除，无键碰撞）。
    /// </para>
    /// 原子状态变更，无需经 DomainService 中转（与 <see cref="SetMarkdown"/> 等必须与 pipeline 完成事务组合的 internal setter 不同）。
    /// <b>前置条件</b>：<see cref="DocumentTypeId"/> 非空（字段挂在文档类型下；两条调用路径均在分类完成后调用）。
    /// </summary>
    public void SetFields(IEnumerable<DocumentFieldValue>? values)
    {
        var incoming = values?.ToList() ?? new List<DocumentFieldValue>();

        _extractedFieldValues.RemoveAll(existing =>
            incoming.All(v => v.FieldDefinitionId != existing.FieldDefinitionId || v.Order != existing.Order));

        foreach (var value in incoming)
        {
            var existing = _extractedFieldValues.FirstOrDefault(
                f => f.FieldDefinitionId == value.FieldDefinitionId && f.Order == value.Order);
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

    /// <summary>
    /// 按位 set / clear 单个待审原因（#284）——原因写入的<b>唯一</b>入口。每个 bit 由唯一一个阶段维护
    /// （UnresolvedClassification←分类阶段，内联于本类方法；MissingRequiredFields←字段抽取阶段，由 Application 层
    /// handler / appservice 在写字段同一 UoW 内评估后调用），用按位操作保证两阶段互不覆盖
    /// （聚合根不暴露整体 setter，防一个阶段误覆盖另一阶段的判定）。
    /// <c>public</c> 是因 MRF 维度的写入点在 Application 层（跨程序集）；可见性放宽但仍约定"每 bit 单阶段维护"。
    /// </summary>
    public void SetReviewReason(DocumentReviewReasons reason, bool present)
    {
        ReviewReasons = present ? (ReviewReasons | reason) : (ReviewReasons & ~reason);
    }

    // 高置信度路径：分类已定 → 清 UnresolvedClassification、处置回 NotReviewed。
    internal void ApplyAutomaticClassificationResult(
        Guid documentTypeId,
        double classificationConfidence)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = Check.Range(classificationConfidence, nameof(classificationConfidence), 0d, 1d);
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: false);
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null; // #284 review-fix：离开 Rejected 处置 → 清陈旧拒绝理由（仅 Rejected 时该有值）
    }

    /// <summary>
    /// 标记分类未定（待人工确认类型）：收回未确认的分类结果，置
    /// <see cref="DocumentReviewReasons.UnresolvedClassification"/>（blocking），避免历史值污染外部读模型。
    /// <para>
    /// 不变量「无已确认类型 ⟹ 无类型绑定字段值」：类型被收回（<see cref="DocumentTypeId"/> = null）后，
    /// 旧的 <see cref="ExtractedFieldValues"/> 不再属于任何已确认类型，必须一并清空——否则出口 DTO / MCP /
    /// 导出会出现「无类型却带字段」的脏读模型（#267：自动重判落到低置信度时首次暴露）。重新确认类型
    /// （<see cref="ConfirmClassification"/> 或高置信度重判 → <c>DocumentClassifiedEto</c> → 字段重抽）会补回字段。
    /// 在聚合根一处收敛此不变量，省得在每个读路径各自按类型过滤（避免特例堆叠）。
    /// 同时清 <see cref="DocumentReviewReasons.MissingRequiredFields"/>——无类型则必填维度不可判定，归零。
    /// </para>
    /// </summary>
    internal void RequestClassificationReview()
    {
        DocumentTypeId = null;
        ClassificationConfidence = 0;
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: true);
        SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null; // #284 review-fix：离开 Rejected 处置 → 清陈旧拒绝理由
        _extractedFieldValues.Clear();
    }

    internal void ConfirmClassification(Guid documentTypeId)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = 1.0;
        // 人工确认类型 → 清 UC；MRF 由随后的字段重抽重算（此处先清，避免旧 schema 的必填判定残留）。
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: false);
        SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
        ReviewDisposition = DocumentReviewDisposition.Confirmed;
        RejectionReason = null; // #284 review-fix：拒绝可恢复——Reclassify/Confirm 后清陈旧拒绝理由
    }

    /// <summary>
    /// 操作员拒绝审核（#284：理由<b>必填</b>）——把 <see cref="ReviewDisposition"/> 置为 Rejected（拒绝的权威信号）、
    /// 写入 <see cref="RejectionReason"/>，并把 <see cref="LifecycleStatus"/> 落到 Failed 作为"宏观不可用"外观。
    /// 保留原文件、Markdown、confidence、字段值与待审原因（<see cref="ReviewReasons"/> 不动）。
    /// <para>
    /// <b>拒绝可恢复，不是终态</b>（#237）：本方法只记录"操作员此刻拒绝"这一事实，不封死文档。操作员后续可对
    /// 同一文档 Reclassify 指派类型——届时 <see cref="ConfirmClassification"/> 把 ReviewDisposition 转回 Confirmed、
    /// 流水线派生回 Ready、<c>DocumentReadyEto</c> 重新发布；下游按 ETO 的 <c>EventTime</c> 单调幂等吸收重发
    /// （见 CLAUDE.md 投递语义）。"曾被拒绝 → 已复审"的轨迹由 ABP 实体审计日志承载，不在聚合根上建吸收态 / Reopen 状态机。
    /// </para>
    /// <para>
    /// <b>lifecycle 派生规则的合法例外</b>：通常 <see cref="LifecycleStatus"/> 由
    /// <see cref="DocumentPipelineRunManager"/> 从 pipeline run 状态派生，此处直接 <see cref="TransitionLifecycle"/>
    /// 到 Failed 是人工审核轴的合法越权。Failed 统一表示"宏观不可用"，<b>原因</b>由细分字段正交说明
    /// （pipeline run = 技术失败；<see cref="ReviewDisposition"/> = Rejected = 操作员拒绝）。
    /// </para>
    /// </summary>
    public void RejectReview(string reason)
    {
        RejectionReason = Check.NotNullOrWhiteSpace(reason, nameof(reason), DocumentConsts.MaxRejectionReasonLength);
        ReviewDisposition = DocumentReviewDisposition.Rejected;
        TransitionLifecycle(DocumentLifecycleStatus.Failed);
    }

    /// <summary>
    /// 迁移 <see cref="LifecycleStatus"/> 并发 <see cref="DocumentLifecycleStatusChangedEvent"/>（仅在状态实变时）。
    /// <para>
    /// <b>无合法转移矩阵是有意的</b>（#237 Finding B）：除 <c>old == new</c> 短路外，任意 <c>(old, new)</c> 跳转都允许，
    /// 包括 <c>Failed → Ready</c>（拒绝后 Reclassify 复活）与 <c>Ready → Processing → Ready</c>（已就绪文档手动重跑流水线）。
    /// 二者都会重新派生 Ready 并重发 <c>DocumentReadyEto</c>——通道层不拦，由下游按 ETO 的 <c>EventTime</c> 单调幂等吸收
    /// （CLAUDE.md 投递语义）。在网关聚合根上不引入硬状态机，是"通道保持简单、幂等交给下游"的有意取舍。
    /// </para>
    /// </summary>
    internal void TransitionLifecycle(DocumentLifecycleStatus newStatus)
    {
        if (LifecycleStatus == newStatus)
            return;

        var oldStatus = LifecycleStatus;
        LifecycleStatus = newStatus;
        AddLocalEvent(new DocumentLifecycleStatusChangedEvent(Id, oldStatus, newStatus));
    }

}

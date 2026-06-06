using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 「留空 AI 兜底选柜」后台作业（#265）：文本提取成功（Markdown 就绪）时由 <c>DocumentTextExtractionBackgroundJob</c>
/// fan-out 一次。独立、一次性、best-effort——守 #194 正交护栏：是 <see cref="Document.CabinetId"/> 的唯一 AI 写入点，
/// 不建 <c>DocumentPipelineRun</c>、不进 Ready 闸门、不可重试；分类 / 字段抽取 pipeline 仍不读写 CabinetId。
/// <para>
/// 仅在 CabinetId 留空时填（人工优先；Begin + Complete 双重门控，后者复检 #257 改派竞态）。三阶段 UoW
/// （Begin 加载 + 门控 + 取候选 → External 无 UoW 跑 LLM → Complete 复检 + 写回），遵循 background-jobs.md。
/// </para>
/// <para>
/// <b>Fail-open 吞下一切异常（含取消）</b>：本作业非 PipelineRun、不可重试，<see cref="ExecuteAsync"/> 的 catch
/// <b>不</b>加 <c>when (ex is not OperationCanceledException)</c> 过滤——否则 provider per-call 超时
/// （<see cref="TaskCanceledException"/> 派生自 <see cref="OperationCanceledException"/>）会逃逸 → 触发 ABP 重试风暴，
/// 与「不可重试」契约相悖。停机及时性由 ambient 取消令牌保证（在飞 LLM 调用随之返回），取消异常本身吞掉、留「未归类」。
/// </para>
/// </summary>
[BackgroundJobName("Paperbase.DocumentCabinetSuggestion")]
public class DocumentCabinetSuggestionBackgroundJob
    : AsyncBackgroundJob<DocumentCabinetSuggestionJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly CabinetSuggestionWorkflow _workflow;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly PaperbaseAIBehaviorOptions _options;
    // ABP BackgroundJobExecuter 在调用 ExecuteAsync 前把作业取消令牌（默认 worker 来源是 host 停机令牌）压入 ambient，
    // 外部慢工作（LLM 调用）据此可在停机时及时取消（与 DocumentTextExtractionBackgroundJob 同源）。
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentCabinetSuggestionBackgroundJob(
        IDocumentRepository documentRepository,
        ICabinetRepository cabinetRepository,
        CabinetSuggestionWorkflow workflow,
        IUnitOfWorkManager unitOfWorkManager,
        ICurrentTenant currentTenant,
        IOptions<PaperbaseAIBehaviorOptions> options,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _cabinetRepository = cabinetRepository;
        _workflow = workflow;
        _unitOfWorkManager = unitOfWorkManager;
        _currentTenant = currentTenant;
        _options = options.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    public override async Task ExecuteAsync(DocumentCabinetSuggestionJobArgs args)
    {
        try
        {
            var workItem = await PrepareAsync(args.DocumentId);
            if (workItem == null)
            {
                // 自门控命中（人工已选 / 无 Markdown / 无候选柜）——静默结束，文档保持「未归类」。
                return;
            }

            var outcome = await SuggestAsync(workItem);

            await ApplyAsync(args.DocumentId, outcome);
        }
        catch (Exception ex)
        {
            // Fail-open：吞下一切（含取消异常）——理由见类注释（不加 OperationCanceledException 过滤，避免 ABP 重试风暴）。
            Logger.LogWarning(ex,
                "AI cabinet suggestion failed for document {DocumentId}; leaving it uncategorized.",
                args.DocumentId);
        }
    }

    /// <summary>Begin 段（短 UoW）：加载文档、自门控、取当前层候选柜。门控命中返回 <c>null</c>。</summary>
    protected virtual async Task<CabinetSuggestionWorkItem?> PrepareAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

        // 护栏 2：人工已选（或上一轮已填）→ 人工优先，AI 不覆盖。
        // 选柜依据内容（#265）——尚无 Markdown 无从判断。
        if (document.CabinetId.HasValue || string.IsNullOrEmpty(document.Markdown))
        {
            await uow.CompleteAsync();
            return null;
        }

        // 候选集按 Document.TenantId 匹配单层（ambient IMultiTenant filter），按柜名稳定排序 + 截断。
        List<Cabinet> candidates;
        using (_currentTenant.Change(document.TenantId))
        {
            var all = await _cabinetRepository.GetListAsync();
            candidates = all
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Take(_options.MaxCabinetsInSuggestionPrompt)
                .ToList();

            // 柜数超上限：被裁掉的是字典序靠后的柜（无优先级概念，least-bad），正确柜可能落在截断外——记 warning 供运维可见。
            if (all.Count > _options.MaxCabinetsInSuggestionPrompt)
            {
                Logger.LogWarning(
                    "Document {DocumentId} layer has {CabinetCount} cabinets, exceeding the suggestion cap {Cap}; "
                    + "candidates beyond the cap are excluded from the prompt.",
                    document.Id, all.Count, _options.MaxCabinetsInSuggestionPrompt);
            }
        }

        await uow.CompleteAsync();

        if (candidates.Count == 0)
        {
            return null;
        }

        return new CabinetSuggestionWorkItem(document.Id, document.TenantId, document.Markdown, candidates);
    }

    /// <summary>External 段（无 UoW）：LLM 选柜。保持 ambient 租户与候选集组装一致（防御未来 workflow 内二次查询）；
    /// 传入 ambient 作业取消令牌，停机时可及时取消 LLM 调用。</summary>
    protected virtual async Task<CabinetSuggestionOutcome> SuggestAsync(CabinetSuggestionWorkItem workItem)
    {
        using (_currentTenant.Change(workItem.TenantId))
        {
            return await _workflow.RunAsync(
                workItem.Candidates, workItem.Markdown, _cancellationTokenProvider.Token);
        }
    }

    /// <summary>Complete 段（短 UoW）：阈值裁决 + 竞态复检 + 写回 CabinetId。</summary>
    protected virtual async Task ApplyAsync(Guid documentId, CabinetSuggestionOutcome outcome)
    {
        // 弃选（无候选 / LLM 弃选 / 编号越界）或置信度不达标 → 不写，保持「未归类」（宁缺毋滥，#265）。
        if (outcome.CabinetId is not { } cabinetId
            || outcome.Confidence < _options.MinCabinetSuggestionConfidence)
        {
            return;
        }

        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

        // 护栏 2 复检：LLM 期间操作员可能已手动改派（#257）——人工优先，不覆盖。
        if (document.CabinetId.HasValue)
        {
            await uow.CompleteAsync();
            return;
        }

        // 复检柜仍在当前层存在（LLM 期间可能被删柜）——不写悬空指向已删柜的 CabinetId。
        using (_currentTenant.Change(document.TenantId))
        {
            var cabinet = await _cabinetRepository.FindAsync(cabinetId);
            if (cabinet == null)
            {
                await uow.CompleteAsync();
                return;
            }
        }

        document.SetCabinet(cabinetId);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    public sealed record CabinetSuggestionWorkItem(
        Guid DocumentId,
        Guid? TenantId,
        string Markdown,
        IReadOnlyList<Cabinet> Candidates);
}

public class DocumentCabinetSuggestionJobArgs
{
    public Guid DocumentId { get; set; }
}

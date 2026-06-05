using System;
using System.Threading.Tasks;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 文档流水线后台作业（文本提取 / 分类）的共享骨架（#216 follow-up #2）。封装两类作业重复的
/// Complete/Fail 阶段前导——短 UoW 内重载 Document + 定位 PipelineRun（按 runId fallback 重建），
/// 以及两侧完全一致的失败收尾。Begin 阶段各作业逻辑不同（候选集组装 / blob 读取），不在基类。
/// <para>
/// 三阶段短 UoW 纪律见 <c>.claude/rules/background-jobs.md</c>：Begin / Complete / Fail 各自独立 UoW，
/// 外部慢工作（OCR / LLM / blob IO）在任何 UoW 之外执行。<see cref="DocumentPipelineRun"/> 自 #216 起是
/// 独立聚合根，经 <see cref="IDocumentPipelineRunRepository"/> 直接读写，不再走 <see cref="Document"/> 聚合。
/// </para>
/// </summary>
public abstract class DocumentPipelineBackgroundJobBase<TArgs> : AsyncBackgroundJob<TArgs>
{
    protected IDocumentRepository DocumentRepository { get; }
    protected IDocumentPipelineRunRepository RunRepository { get; }
    protected DocumentPipelineRunManager PipelineRunManager { get; }
    protected DocumentPipelineRunAccessor PipelineRunAccessor { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }

    protected DocumentPipelineBackgroundJobBase(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager)
    {
        DocumentRepository = documentRepository;
        RunRepository = runRepository;
        PipelineRunManager = pipelineRunManager;
        PipelineRunAccessor = pipelineRunAccessor;
        UnitOfWorkManager = unitOfWorkManager;
    }

    /// <summary>
    /// Complete / Fail 阶段共享加载：取 Document（不 eager-load runs），按 <paramref name="runId"/> 定位本作业
    /// 的 run，找不到则经 <see cref="DocumentPipelineRunAccessor.BeginOrStartAsync"/> 按该 runId fallback 重建。
    /// 调用方负责在自己的短 UoW 内调用并提交。
    /// <para>
    /// <paramref name="includeFieldValues"/> = true 时 eager-load <see cref="Document.ExtractedFieldValues"/>——
    /// 仅分类作业完成阶段需要：低置信度落 <c>RequestClassificationReview</c> 会清空类型绑定字段（#267），
    /// 集合须在场 EF 才会真正删除子行。文本提取 / 失败收尾路径用默认 false，不付出多余 JOIN。
    /// </para>
    /// </summary>
    protected virtual async Task<(Document Document, DocumentPipelineRun Run)> LoadDocumentAndRunAsync(
        Guid documentId,
        Guid runId,
        string pipelineCode,
        bool includeFieldValues = false)
    {
        var document = includeFieldValues
            ? await DocumentRepository.FindWithFieldValuesAsync(documentId)
                ?? throw new EntityNotFoundException(typeof(Document), documentId)
            : await DocumentRepository.GetAsync(documentId, includeDetails: false);
        var run = await RunRepository.FindAsync(runId)
            ?? await PipelineRunAccessor.BeginOrStartAsync(document, runId, pipelineCode);
        return (document, run);
    }

    /// <summary>
    /// 失败收尾：独立短 UoW 内重载 Document + run，标记 run 失败（Manager 内部据此派生 LifecycleStatus），
    /// 持久化 Document 主行并提交。两类作业的失败路径完全一致（仅 <paramref name="pipelineCode"/> 不同），
    /// 故上提至基类。调用方在 catch 中调用后通常 re-throw 以触发 ABP 后台作业重试。
    /// </summary>
    protected virtual async Task FailRunAsync(
        Guid documentId,
        Guid runId,
        string errorMessage,
        string pipelineCode)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var (document, run) = await LoadDocumentAndRunAsync(documentId, runId, pipelineCode);
        await PipelineRunManager.FailAsync(document, run, errorMessage);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }
}

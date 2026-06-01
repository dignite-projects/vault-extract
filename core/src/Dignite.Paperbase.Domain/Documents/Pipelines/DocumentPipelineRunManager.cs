using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.DocumentTypes;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Services;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 流水线执行记录的统一入口。
/// 负责创建 Run、驱动状态流转、在每次状态变化后重新派生 Document.LifecycleStatus。
/// 所有向 Document 写入流水线结果的代码都必须通过此服务。
/// <para>
/// 设计选择：本 manager <b>不查 DocumentType</b>——typeCode 校验责任在 AppService（调用方负责
/// 通过 <c>IDocumentTypeRepository.FindByTypeCodeAsync</c> 加载 <see cref="DocumentType"/>
/// 并传给本 manager）。这避免热路径重复查 DB（BackgroundJob 已 load 一次，manager 再查一次浪费），
/// 并让 Domain 层 manager 不耦合上层数据访问关心。
/// </para>
/// <para>
/// #216 起 <see cref="DocumentPipelineRun"/> 是独立聚合根：状态流转通过 <see cref="IDocumentPipelineRunRepository"/>
/// 持久化；<see cref="DeriveLifecycleAsync"/> 查仓储算最新 run。EF Core 默认 LINQ 查 DB 看不到本 UoW 内尚未
/// flush 的 Insert/Modify，故 <see cref="IDocumentPipelineRunRepository.GetLatestRunsByCodesAsync"/> 在仓储内
/// 合并 change-tracker 的 Local entries 补上 post-change 视图——派生逻辑因此无需调用方传入"刚改动的 run"。
/// </para>
/// <para>
/// <b>AttemptNumber 并发安全</b>（#216 D2）：拆分前 Document 主行 UPDATE 提供隐式行级锁；拆分后无此互斥。
/// 防御分两层：
/// (1) DB 层 <b>unique index</b> <c>(DocumentId, PipelineCode, AttemptNumber)</c> 硬约束；
/// (2) <see cref="QueueAsync"/> 内捕获撞键异常 → 经
/// <see cref="IDocumentPipelineRunRepository.DetachAsync"/> 从 EF tracker 移除失败实体 → 重新读 latest +
/// 重算 attempt + 重试（最多 <see cref="MaxAttemptNumberRetries"/> 次）。HTTP 同步路径（<c>RetryPipelineAsync</c>）
/// 不再裸 500。仅当超过重试上限才抛——此时通常是真实病态并发或 DB 不可用。
/// </para>
/// </summary>
public class DocumentPipelineRunManager : DomainService
{
    /// <summary>AttemptNumber 撞 unique 索引时 QueueAsync 的最大重试次数（含首发；总尝试上限）。</summary>
    public const int MaxAttemptNumberRetries = 3;

    private readonly IDocumentPipelineRunRepository _runRepo;

    public DocumentPipelineRunManager(IDocumentPipelineRunRepository runRepo)
    {
        _runRepo = runRepo;
    }

    public virtual async Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        Exception? lastCollision = null;
        DocumentPipelineRun? failedAttempt = null;

        for (var attempt = 0; attempt < MaxAttemptNumberRetries; attempt++)
        {
            // Retry 前先把上一次撞键失败的实体从 EF tracker 移除——SaveChanges 失败后 EF 把它留在 Added 状态，
            // 不 detach 的话下一次 SaveChanges 会重新尝试同一个失败实体（且持有原冲突的 AttemptNumber）。
            if (failedAttempt != null)
            {
                await _runRepo.DetachAsync(failedAttempt);
                failedAttempt = null;
            }

            var latest = await _runRepo.FindLatestByDocumentAndCodeAsync(document.Id, pipelineCode);
            var attemptNumber = (latest?.AttemptNumber ?? 0) + 1;

            var run = new DocumentPipelineRun(
                pipelineRunId ?? GuidGenerator.Create(),
                document.Id,
                document.TenantId,
                pipelineCode,
                attemptNumber);

            run.MarkPending(Clock.Now);

            try
            {
                // autoSave:true → 触发本 UoW 立即 SaveChanges，撞键当场抛 DbUpdateException 而非延后到外层 commit。
                // 同 UoW / 同事务：成功的 INSERT 仍由外层 UoW commit 才真正可见给其他事务；失败 / 外层回滚一并撤销。
                await _runRepo.InsertAsync(run, autoSave: true);
                await DeriveLifecycleAsync(document);
                return run;
            }
            catch (Exception ex) when (IsAttemptNumberUniqueViolation(ex)
                                       && attempt < MaxAttemptNumberRetries - 1)
            {
                Logger.LogWarning(ex,
                    "AttemptNumber collision on document {DocumentId} pipeline {PipelineCode} attempt {AttemptNumber}; retrying ({Retry}/{Max}).",
                    document.Id, pipelineCode, attemptNumber, attempt + 1, MaxAttemptNumberRetries);
                lastCollision = ex;
                failedAttempt = run;
            }
        }

        throw new BusinessException(PaperbaseErrorCodes.Pipeline.AttemptNumberRetryExhausted, innerException: lastCollision)
            .WithData("DocumentId", document.Id)
            .WithData("PipelineCode", pipelineCode);
    }

    /// <summary>
    /// 判断异常链是否表征 (DocumentId, PipelineCode, AttemptNumber) unique 索引撞键。
    /// 不引用 EF Core / Microsoft.Data.SqlClient（Domain 层禁止）——按异常链 message 字符串 / SQL Server 错误码侦测，
    /// 误判面窄到只剩"消息恰好含 UNIQUE / duplicate key / 2601 / 2627 之一"的其他场景，可接受。
    /// </summary>
    private static bool IsAttemptNumberUniqueViolation(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var msg = current.Message ?? string.Empty;
            if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("2601") || msg.Contains("2627"))
            {
                return true;
            }
        }
        return false;
    }

    public virtual async Task<DocumentPipelineRun> StartAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        var run = await QueueAsync(document, pipelineCode, pipelineRunId);
        await BeginAsync(document, run);
        return run;
    }

    public virtual Task BeginAsync(Document document, DocumentPipelineRun run)
    {
        run.MarkRunning(Clock.Now);
        return PersistAndDeriveAsync(document, run, publishCompletion: false);
    }

    public virtual Task CompleteAsync(Document document, DocumentPipelineRun run)
    {
        run.MarkSucceeded(Clock.Now);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    public virtual Task FailAsync(Document document, DocumentPipelineRun run, string errorMessage)
    {
        run.MarkFailed(Clock.Now, errorMessage);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    /// <summary>
    /// 记录文本提取结果、写入语言 + provenance 元数据并完成 Run。
    /// <paramref name="markdown"/> 是流水线唯一的文本载荷（数字版与 OCR 路径都已统一输出 Markdown）；
    /// 下游需要纯文本时通过 <see cref="MarkdownStripper.Strip"/> 投影。
    /// <para>
    /// #210：<paramref name="language"/>（终结 write-never 死字段）与
    /// <paramref name="extractionMetadata"/>（provenance：provider 名 + 原生 payload 归档 manifest，
    /// 原始 payload 已由调用方在 External 段归档进 blob）与 Markdown / Title 同事务原子写入。
    /// 参数可空且有默认值——既有调用方无需改动。
    /// </para>
    /// </summary>
    public virtual Task CompleteTextExtractionAsync(
        Document document,
        DocumentPipelineRun run,
        string markdown,
        string? title,
        string? language = null,
        DocumentTextExtractionMetadata? extractionMetadata = null)
    {
        document.SetMarkdown(markdown);
        document.SetTitle(title);
        document.SetLanguage(language);
        document.SetExtractionMetadata(extractionMetadata);
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 记录分类结果并完成 Run（高置信度路径）。
    /// <see cref="Document.ClassificationReason"/> 在此路径下固定为 null；
    /// AI 的分类理由仅在低置信度路径（<see cref="CompleteClassificationWithLowConfidenceAsync"/>）写入。
    /// <para>
    /// 调用方负责传入已加载的 <paramref name="typeDef"/>（来自 <c>IDocumentTypeRepository.FindByTypeCodeAsync</c>），
    /// manager 不再查 DB；调用方必须确保 <c>typeDef.TenantId == document.TenantId</c>（单层精确匹配）。
    /// </para>
    /// </summary>
    public virtual Task CompleteClassificationAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentType typeDef,
        double confidenceScore)
    {
        Check.NotNull(typeDef, nameof(typeDef));
        document.ApplyAutomaticClassificationResult(typeDef.Id, confidenceScore);
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 分类置信度不足：完成 Run 并将文档标记为待人工审核。
    /// <see cref="Document.ClassificationReason"/> 写入 AI 的分类理由（reason）；
    /// Run.StatusMessage 保持 null（<see cref="DocumentPipelineRun.MarkSucceeded"/> 不写 StatusMessage），
    /// 避免与技术错误信息混淆。
    /// 置信度信号由 <see cref="Document.ReviewStatus"/> = PendingReview 表达，不再记录在 Run 上。
    /// </summary>
    public virtual Task CompleteClassificationWithLowConfidenceAsync(
        Document document,
        DocumentPipelineRun run,
        string? reason = null,
        IReadOnlyList<PipelineRunCandidate>? candidates = null)
    {
        document.RequestClassificationReview(reason);

        if (candidates is { Count: > 0 })
        {
            run.SetProperty(
                PipelineRunExtraPropertyNames.ClassificationCandidates,
                candidates);
        }

        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 人工确认文档类型：写入分类结果、标记已审核、完成 Run。置信度固定为 1.0。
    /// 人工覆盖信号由 <see cref="Document.ReviewStatus"/> = Reviewed 表达。
    /// 该字面量与 Domain.Shared 层 <c>ClassificationDefaults.ManualClassificationConfidence</c>
    /// 同步维护（Domain 不依赖 Abstractions，故此处硬编码）。
    /// <para>
    /// 调用方负责传入已加载的 <paramref name="typeDef"/>；manager 不再查 DB。
    /// </para>
    /// </summary>
    public virtual Task CompleteManualClassificationAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentType typeDef)
    {
        Check.NotNull(typeDef, nameof(typeDef));
        document.ConfirmClassification(typeDef.Id);
        return CompleteAsync(document, run);
    }

    public virtual Task SkipAsync(Document document, DocumentPipelineRun run, string reason)
    {
        run.MarkSkipped(Clock.Now, reason);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    /// <summary>
    /// 取该 pipeline 最近一次 run 并校验其可重试性——仅 <see cref="PipelineRunStatus.Failed"/> 可重试。
    /// 不存在任何 run → <c>Pipeline.NeverRan</c>；Pending/Running → <c>Pipeline.RetryInProgress</c>（并发护栏）；
    /// Succeeded/Skipped → <c>Pipeline.NotRetryable</c>。校验通过返回该 failed run，调用方据其
    /// <see cref="DocumentPipelineRun.AttemptNumber"/> 记审计日志后再 <see cref="QueueAsync"/> 触发重试。
    /// <para>
    /// retry 状态机判定是 run 聚合的 domain 关注点，集中在 manager（已持有 <see cref="IDocumentPipelineRunRepository"/>），
    /// 让 AppService 不直接查 run 仓储（#216 follow-up #6）。PipelineCode 合法性是输入层校验，留在调用方。
    /// </para>
    /// </summary>
    public virtual async Task<DocumentPipelineRun> EnsureRetryableAsync(Guid documentId, string pipelineCode)
    {
        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(documentId, pipelineCode);
        if (latestRun == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.Pipeline.NeverRan)
                .WithData("PipelineCode", pipelineCode);
        }

        switch (latestRun.Status)
        {
            case PipelineRunStatus.Pending:
            case PipelineRunStatus.Running:
                throw new BusinessException(PaperbaseErrorCodes.Pipeline.RetryInProgress)
                    .WithData("PipelineCode", pipelineCode);
            case PipelineRunStatus.Succeeded:
            case PipelineRunStatus.Skipped:
                throw new BusinessException(PaperbaseErrorCodes.Pipeline.NotRetryable)
                    .WithData("PipelineCode", pipelineCode)
                    .WithData("Status", latestRun.Status.ToString());
        }

        return latestRun;
    }

    /// <summary>
    /// Shared tail of every state transition: persist the run via repo, optionally fire the run-completed
    /// LocalEvent, then re-derive Document.LifecycleStatus. BeginAsync skips the event (run is just starting,
    /// not completing); Complete/Fail/Skip all publish it.
    /// </summary>
    private async Task PersistAndDeriveAsync(
        Document document,
        DocumentPipelineRun run,
        bool publishCompletion)
    {
        await _runRepo.UpdateAsync(run);

        if (publishCompletion)
        {
            run.PublishRunCompletedEvent();
        }

        await DeriveLifecycleAsync(document);
    }

    /// <summary>
    /// 根据所有关键流水线的最新 Run 派生 Document.LifecycleStatus。
    /// <para>
    /// 最新 run 由 <see cref="IDocumentPipelineRunRepository.GetLatestRunsByCodesAsync"/> 提供，该仓储已
    /// 合并本 UoW 内尚未 flush 的 change-tracker 实体（EFCore 实现 peek Local entries；in-memory fake 因
    /// 持有 run 引用天然可见）。故此处直接消费仓储结果即是 post-change 视图，无需调用方再传入"刚改动的 run"。
    /// </para>
    /// </summary>
    protected virtual async Task DeriveLifecycleAsync(Document document)
    {
        var latestRuns = await _runRepo.GetLatestRunsByCodesAsync(
            document.Id, PaperbasePipelines.KeyPipelines);

        var derivedStatus = DocumentLifecycleStatus.Processing;
        var allSucceeded = true;

        foreach (var pipelineCode in PaperbasePipelines.KeyPipelines)
        {
            if (!latestRuns.TryGetValue(pipelineCode, out var latestRun))
            {
                allSucceeded = false;
                continue;
            }

            if (latestRun.Status == PipelineRunStatus.Failed)
            {
                derivedStatus = DocumentLifecycleStatus.Failed;
                allSucceeded = false;
                break;
            }

            if (latestRun.Status != PipelineRunStatus.Succeeded)
            {
                allSucceeded = false;
            }
        }

        if (derivedStatus != DocumentLifecycleStatus.Failed &&
            allSucceeded &&
            document.DocumentTypeId.HasValue)
        {
            derivedStatus = DocumentLifecycleStatus.Ready;
        }

        document.TransitionLifecycle(derivedStatus);
    }
}

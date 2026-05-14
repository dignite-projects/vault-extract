using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L2 自动触发：执行 <see cref="RelationDiscoveryService.DiscoverAsync"/>，
/// 用 <see cref="DocumentPipelineRun"/> 跟踪运行状态以便观察 / 后续诊断。
///
/// <para>
/// 短 UoW 模式（参见 <c>.claude/rules/background-jobs.md</c>）：
/// <list type="number">
/// <item>Begin：UoW1 加载 Document，标记 PipelineRun 为 Running，提交。</item>
/// <item>Discovery：UoW2 调用 <see cref="RelationDiscoveryService"/>，创建 AiSuggested 关系，提交。</item>
/// <item>Complete / Fail：UoW3 重新加载 Document，标记 PipelineRun 状态，提交。</item>
/// </list>
/// L2 本身只做 DB 查询（无 LLM / 文件 IO / 长 CPU），但仍然分阶段——
/// 保持与其他 pipeline 一致的运行模型，便于将来加 telemetry / 重试。
/// </para>
///
/// <para>
/// 失败处理：DiscoverAsync 内部已对 provider 异常做隔离（见 #118 commit ffe745a）；
/// 此层 try/catch 兜底捕获基础设施异常（DB 连接断开 / 序列化错误），
/// 不会因为某个有 bug 的 provider 把整个 PipelineRun 拖成 Failed。
/// </para>
/// </summary>
[BackgroundJobName("Paperbase.RelationDiscovery")]
public class RelationDiscoveryBackgroundJob
    : AsyncBackgroundJob<RelationDiscoveryJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineRunAccessor _pipelineRunAccessor;
    private readonly RelationDiscoveryService _discoveryService;
    private readonly SemanticRelationDiscoveryService _semanticDiscoveryService;
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    public RelationDiscoveryBackgroundJob(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        RelationDiscoveryService discoveryService,
        SemanticRelationDiscoveryService semanticDiscoveryService,
        RelationDiscoveryTelemetryRecorder telemetry,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _pipelineRunAccessor = pipelineRunAccessor;
        _discoveryService = discoveryService;
        _semanticDiscoveryService = semanticDiscoveryService;
        _telemetry = telemetry;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _aiOptions = aiOptions.Value;
    }

    public override async Task ExecuteAsync(RelationDiscoveryJobArgs args)
    {
        // Codex review fix [high] "Tenant context dropped": providers depend on ABP's
        // IMultiTenant ambient filter to scope queries by tenant. Background-job dispatchers
        // don't always restore CurrentTenant from job args (depends on dispatcher config and
        // distributed-event bus). Explicit Change(args.TenantId) makes this deterministic
        // regardless of dispatch path — matches ContractDocumentHandler's pattern for the
        // same DocumentClassifiedEto.
        using (_currentTenant.Change(args.TenantId))
        {
            await ExecuteCoreAsync(args);
        }
    }

    protected virtual async Task ExecuteCoreAsync(RelationDiscoveryJobArgs args)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var workItem = await BeginRunAsync(args);
        if (workItem == null)
        {
            // Document hard-deleted; no PipelineRun to mark, but still record run metric for ops.
            totalStopwatch.Stop();
            _telemetry.RecordRun(new RelationDiscoveryRunMetrics
            {
                DocumentId = args.DocumentId,
                Result = RelationDiscoveryRunResult.DocumentMissing,
                TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
            });
            return;
        }

        DiscoveryOutcome outcome;
        try
        {
            outcome = await DiscoverAsync(workItem.DocumentId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "L2 RelationDiscovery failed for document {DocumentId}. PipelineRun marked failed; document lifecycle unchanged (non-key pipeline).",
                workItem.DocumentId);
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message);
            totalStopwatch.Stop();
            _telemetry.RecordRun(new RelationDiscoveryRunMetrics
            {
                DocumentId = workItem.DocumentId,
                Result = RelationDiscoveryRunResult.Failed,
                FailureReason = ex.GetType().Name,
                TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
            });
            return;
        }

        await CompleteRunAsync(workItem.DocumentId, workItem.RunId, outcome.TotalCreated);
        totalStopwatch.Stop();
        _telemetry.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = workItem.DocumentId,
            Result = RelationDiscoveryRunResult.Succeeded,
            L2CreatedCount = outcome.L2Created,
            L3Invoked = outcome.L3Invoked,
            L3CreatedCount = outcome.L3Invoked ? outcome.L3Created : null,
            L3CandidatesRecalled = outcome.L3Invoked ? outcome.L3CandidatesRecalled : null,
            L3CandidatesEvaluated = outcome.L3Invoked ? outcome.L3CandidatesEvaluated : null,
            L3CircuitBroken = outcome.L3CircuitBroken,
            L2DurationMs = outcome.L2DurationMs,
            L3DurationMs = outcome.L3Invoked ? outcome.L3DurationMs : null,
            TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
        });
    }

    protected virtual async Task<DiscoveryWorkItem?> BeginRunAsync(RelationDiscoveryJobArgs args)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.FindAsync(args.DocumentId, includeDetails: true);
        if (document == null)
        {
            // Document was hard-deleted between event publish and job pickup — silently drop.
            // No PipelineRun to mark; Document carrying the run is gone.
            Logger.LogInformation(
                "L2 RelationDiscovery: document {DocumentId} no longer exists; dropping job.",
                args.DocumentId);
            await uow.CompleteAsync();
            return null;
        }

        var run = await _pipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, PaperbasePipelines.RelationDiscovery);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return new DiscoveryWorkItem(run.Id, document.Id);
    }

    protected virtual async Task<DiscoveryOutcome> DiscoverAsync(Guid documentId)
    {
        // L2: structured fan-out across business-module providers. Cheap (DB queries only).
        // L2 writes commit in a dedicated UoW (autoSave: false on inserts ⇒ uow.CompleteAsync()).
        int l2Count;
        var l2Stopwatch = Stopwatch.StartNew();
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var created = await _discoveryService.DiscoverAsync(documentId);
            await uow.CompleteAsync();
            l2Count = created.Count;
        }
        l2Stopwatch.Stop();

        // L3: always invoked when enabled, regardless of L2 outcome (codex review fix [high] R1
        // "L2/L3 coverage decoupling"). L2 finds documents sharing exact identifiers (contract
        // number, invoice number); L3 finds semantically-related documents WITHOUT shared
        // identifiers (meeting notes referencing a contract, supplement agreements, etc.) — these
        // are non-overlapping relation kinds. Letting L2 short-circuit L3 systematically blinded
        // the system to L3-only relations whenever a document had any L2 hit through any provider.
        // Duplicate pairs are filtered upstream by SemanticRelationDiscoveryService.GetAlreadyLinkedAsync,
        // which already excludes any document already linked to the source by ANY DocumentRelation
        // (including the AiSuggested rows L2 just wrote in this same job).
        //
        // Gate on EnableSemanticRelationDiscovery here (not inside SemanticDiscoveryService) so that
        // L3Invoked telemetry reflects reality: true iff L3 was attempted, false iff disabled by config.
        // NOT wrapped in an outer UoW: SemanticRelationDiscoveryService uses autoSave: true on
        // each relation insert (per-call implicit UoW), so LLM calls between candidates run with
        // no ambient UoW — satisfies the "no DB connection during external work" rule.
        if (!_aiOptions.EnableSemanticRelationDiscovery)
        {
            return new DiscoveryOutcome(
                L2Created: l2Count,
                L3Invoked: false,
                L3Created: 0,
                L3CandidatesRecalled: 0,
                L3CandidatesEvaluated: 0,
                L3CircuitBroken: false,
                L2DurationMs: l2Stopwatch.Elapsed.TotalMilliseconds,
                L3DurationMs: 0);
        }

        var l3Stopwatch = Stopwatch.StartNew();
        var l3Outcome = await _semanticDiscoveryService.DiscoverAsync(documentId);
        l3Stopwatch.Stop();

        return new DiscoveryOutcome(
            L2Created: l2Count,
            L3Invoked: true,
            L3Created: l3Outcome.Relations.Count,
            L3CandidatesRecalled: l3Outcome.CandidatesRecalled,
            L3CandidatesEvaluated: l3Outcome.CandidatesEvaluated,
            L3CircuitBroken: l3Outcome.CircuitBroken,
            L2DurationMs: l2Stopwatch.Elapsed.TotalMilliseconds,
            L3DurationMs: l3Stopwatch.Elapsed.TotalMilliseconds);
    }

    protected sealed record DiscoveryOutcome(
        int L2Created,
        bool L3Invoked,
        int L3Created,
        int L3CandidatesRecalled,
        int L3CandidatesEvaluated,
        bool L3CircuitBroken,
        double L2DurationMs,
        double L3DurationMs)
    {
        public int TotalCreated => L2Created + L3Created;
    }

    protected virtual async Task CompleteRunAsync(Guid documentId, Guid runId, int createdCount)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.RelationDiscovery);

        await _pipelineRunManager.CompleteAsync(document, run);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        Logger.LogInformation(
            "L2 RelationDiscovery: document {DocumentId} run {RunId} succeeded; created {CreatedCount} AiSuggested relations.",
            documentId, runId, createdCount);
    }

    protected virtual async Task FailRunAsync(Guid documentId, Guid runId, string errorMessage)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.RelationDiscovery);

        await _pipelineRunManager.FailAsync(document, run, errorMessage);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    protected sealed record DiscoveryWorkItem(Guid RunId, Guid DocumentId);
}

public class RelationDiscoveryJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }

    /// <summary>
    /// Tenant id captured at enqueue time (from <c>DocumentClassifiedEto.TenantId</c> via
    /// <c>RelationDiscoveryEventHandler</c>). The job restores this explicitly via
    /// <c>CurrentTenant.Change</c> in <see cref="RelationDiscoveryBackgroundJob.ExecuteAsync"/>
    /// so providers / repositories see the correct ambient tenant filter regardless of
    /// dispatcher behavior. Codex review fix [high] "Tenant context dropped".
    /// </summary>
    public Guid? TenantId { get; set; }
}

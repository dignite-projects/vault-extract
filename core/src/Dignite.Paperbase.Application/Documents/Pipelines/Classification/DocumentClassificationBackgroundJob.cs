using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.Classification;

[BackgroundJobName("Paperbase.DocumentClassification")]
public class DocumentClassificationBackgroundJob
    : AsyncBackgroundJob<DocumentClassificationJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineRunAccessor _pipelineRunAccessor;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentClassificationBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        DocumentClassificationWorkflow workflow,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _pipelineRunManager = pipelineRunManager;
        _pipelineRunAccessor = pipelineRunAccessor;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _aiOptions = aiOptions.Value;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public override async Task ExecuteAsync(DocumentClassificationJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        try
        {
            var outcome = await ClassifyAsync(workItem);
            await CompleteRunAsync(workItem, outcome);
        }
        catch (Exception ex)
        {
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message);
            throw;
        }
    }

    private async Task<ClassificationWorkItem> BeginRunAsync(DocumentClassificationJobArgs args)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(args.DocumentId);

        // 候选集组装：按 Document.TenantId 匹配单层文档类型（解读 X + 没有继承关系）。
        // Host 文档用 TenantId IS NULL 类型；租户文档用对应租户类型；按 Priority DESC + 截断。
        List<DocumentType> candidates;
        using (_currentTenant.Change(document.TenantId))
        {
            var visible = await _documentTypeRepository.GetByTenantAsync();
            candidates = visible
                .Take(_aiOptions.MaxDocumentTypesInClassificationPrompt)
                .ToList();
        }

        var run = await _pipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, PaperbasePipelines.Classification);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return new ClassificationWorkItem(run.Id, document.Id, document.TenantId, document.Markdown ?? string.Empty, candidates);
    }

    private async Task<DocumentClassificationOutcome> ClassifyAsync(ClassificationWorkItem workItem)
    {
        // 防御性深度：LLM 调用本身不查 DB（无跨租户泄漏风险），但保持 ambient tenant 与
        // FieldExtractionEventHandler 一致——未来若 workflow 内增 telemetry / cache / 二次查询，
        // 不会因为 ambient context 漂移而绕过隔离。
        using (_currentTenant.Change(workItem.TenantId))
        {
            try
            {
                return await _workflow.RunAsync(workItem.Candidates, workItem.Markdown);
            }
            catch (Exception ex) when (IsSchemaDeserializationError(ex))
            {
                Logger.LogWarning(ex,
                    "AI classification response failed JSON deserialization for document {DocumentId}; routing to PendingReview.",
                    workItem.DocumentId);
                return new DocumentClassificationOutcome
                {
                    TypeCode = null,
                    ConfidenceScore = 0,
                    Reason = "AI response could not be parsed (schema drift)."
                };
            }
        }
    }

    private async Task CompleteRunAsync(
        ClassificationWorkItem workItem,
        DocumentClassificationOutcome outcome)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(workItem.DocumentId);
        var run = document.GetRun(workItem.RunId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, workItem.RunId, PaperbasePipelines.Classification);

        await ApplyClassificationResultAsync(document, run, outcome);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private async Task FailRunAsync(Guid documentId, Guid runId, string errorMessage)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(documentId);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.Classification);

        await _pipelineRunManager.FailAsync(document, run, errorMessage);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    private async Task ApplyClassificationResultAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentClassificationOutcome outcome)
    {
        // 字段架构 v2：从 DB 查 type definition（按 Document.TenantId 精确匹配单层）
        DocumentType? typeDef = null;
        if (!string.IsNullOrEmpty(outcome.TypeCode))
        {
            using (_currentTenant.Change(document.TenantId))
            {
                typeDef = await _documentTypeRepository.FindByTypeCodeAsync(outcome.TypeCode);
            }
        }

        if (typeDef != null && outcome.ConfidenceScore >= typeDef.ConfidenceThreshold)
        {
            await _pipelineRunManager.CompleteClassificationAsync(
                document, run, typeDef, outcome.ConfidenceScore);

            await _distributedEventBus.PublishAsync(
                new DocumentClassifiedEto
                {
                    DocumentId = document.Id,
                    TenantId = document.TenantId,
                    EventTime = _clock.Now,
                    DocumentTypeCode = typeDef.TypeCode,
                    ClassificationConfidence = outcome.ConfidenceScore
                });

            return;
        }

        await _pipelineRunManager.CompleteClassificationWithLowConfidenceAsync(
            document, run, outcome.Reason, outcome.Candidates);
    }

    private sealed record ClassificationWorkItem(
        Guid RunId,
        Guid DocumentId,
        Guid? TenantId,
        string Markdown,
        IReadOnlyList<DocumentType> Candidates);
}

public class DocumentClassificationJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }
}

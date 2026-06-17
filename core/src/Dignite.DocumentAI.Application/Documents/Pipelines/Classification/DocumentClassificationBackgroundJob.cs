using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.Classification;

[BackgroundJobName("DocumentAI.DocumentClassification")]
public class DocumentClassificationBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentClassificationJobArgs>, ITransientDependency
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly DocumentAIBehaviorOptions _aiOptions;
    private readonly ICurrentTenant _currentTenant;

    public DocumentClassificationBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        IDocumentTypeRepository documentTypeRepository,
        DocumentClassificationWorkflow workflow,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        IOptions<DocumentAIBehaviorOptions> aiOptions,
        ICurrentTenant currentTenant)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _documentTypeRepository = documentTypeRepository;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _aiOptions = aiOptions.Value;
        _currentTenant = currentTenant;
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
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message, DocumentAIPipelines.Classification);
            throw;
        }
    }

    private async Task<ClassificationWorkItem> BeginRunAsync(DocumentClassificationJobArgs args)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var document = await DocumentRepository.GetAsync(args.DocumentId, includeDetails: false);

        // Candidate assembly: match document types in the single layer selected by Document.TenantId,
        // never cross-layer union; order by Priority DESC and truncate.
        List<DocumentType> candidates;
        using (_currentTenant.Change(document.TenantId))
        {
            var visible = await _documentTypeRepository.GetListAsync();
            candidates = visible
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.TypeCode)
                .Take(_aiOptions.MaxDocumentTypesInClassificationPrompt)
                .ToList();
        }

        var run = await PipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, DocumentAIPipelines.Classification);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return new ClassificationWorkItem(run.Id, document.Id, document.TenantId, document.Markdown ?? string.Empty, candidates);
    }

    private async Task<DocumentClassificationOutcome> ClassifyAsync(ClassificationWorkItem workItem)
    {
        // Defense-in-depth: the LLM call itself does not query the DB, so it has no cross-tenant leak
        // risk, but keep the ambient tenant aligned with FieldExtractionEventHandler. If telemetry /
        // cache / secondary queries are added inside the workflow later, ambient context drift will
        // not bypass isolation.
        using (_currentTenant.Change(workItem.TenantId))
        {
            try
            {
                return await _workflow.RunAsync(workItem.Candidates, workItem.Markdown);
            }
            catch (Exception ex) when (IsSchemaDeserializationError(ex))
            {
                Logger.LogWarning(ex,
                    "AI classification response failed JSON deserialization for document {DocumentId}; routing to manual review.",
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
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        // includeFieldValues:true because the low-confidence path clears type-bound fields (#267
        // invariant), and EF needs the collection present to delete child rows.
        var (document, run) = await LoadDocumentAndRunAsync(
            workItem.DocumentId, workItem.RunId, DocumentAIPipelines.Classification, includeFieldValues: true);

        await ApplyClassificationResultAsync(document, run, outcome);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    private async Task ApplyClassificationResultAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentClassificationOutcome outcome)
    {
        // #346: a container dominates the type guess. A parent that bundles several independent documents runs no
        // field extraction itself: mark it a container, complete the run as succeeded, and — crucially — do NOT
        // publish DocumentClassifiedEto, so FieldExtractionEventHandler never cascades (the race-free suppression
        // is simply never emitting the trigger event). MarkAsContainer leaves DocumentTypeId null and sets no
        // UnresolvedClassification reason, so the container is not sent to the operator review queue and — with
        // both key pipelines succeeded and no blocking reason — derives straight to Ready (Design A).
        if (outcome.IsContainer)
        {
            await PipelineRunManager.CompleteClassificationAsContainerAsync(document, run);
            return;
        }

        // Field architecture v2: look up the type definition from DB, exactly matching the single
        // layer selected by Document.TenantId.
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
            await PipelineRunManager.CompleteClassificationAsync(
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

        await PipelineRunManager.CompleteClassificationWithLowConfidenceAsync(
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

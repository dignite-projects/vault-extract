using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents.Pipelines.Segmentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
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
    private readonly IBackgroundJobManager _backgroundJobManager;
    // #371: classification reads the MARKED Markdown (with [Image OCR] sentinels) so the embedded-document signal
    // can recognize figure spans; the blob is the same artifact the unified pass reads.
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;

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
        ICurrentTenant currentTenant,
        IBackgroundJobManager backgroundJobManager,
        IBlobContainer<DocumentAIDocumentContainer> blobContainer)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _documentTypeRepository = documentTypeRepository;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _aiOptions = aiOptions.Value;
        _currentTenant = currentTenant;
        _backgroundJobManager = backgroundJobManager;
        _blobContainer = blobContainer;
    }

    public override async Task ExecuteAsync(DocumentClassificationJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        try
        {
            // #371: classify over the MARKED Markdown (external blob read, no UoW) so the embedded-document signal
            // sees the [Image OCR] figure spans; fall back to the clean Document.Markdown when there is no artifact.
            var marked = await TryReadMarkedMarkdownAsync(workItem.DocumentId) ?? workItem.Markdown;
            var outcome = await ClassifyAsync(workItem, marked);
            await CompleteRunAsync(workItem, outcome, ImageOcrMarkup.Contains(marked), marked.Length);
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

    private async Task<DocumentClassificationOutcome> ClassifyAsync(ClassificationWorkItem workItem, string markdown)
    {
        // Defense-in-depth: the LLM call itself does not query the DB, so it has no cross-tenant leak
        // risk, but keep the ambient tenant aligned with FieldExtractionEventHandler. If telemetry /
        // cache / secondary queries are added inside the workflow later, ambient context drift will
        // not bypass isolation.
        using (_currentTenant.Change(workItem.TenantId))
        {
            try
            {
                return await _workflow.RunAsync(workItem.Candidates, markdown);
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
        DocumentClassificationOutcome outcome,
        bool hasFigures,
        int markedLength)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        // includeFieldValues:true because the low-confidence path clears type-bound fields (#267
        // invariant), and EF needs the collection present to delete child rows.
        var (document, run) = await LoadDocumentAndRunAsync(
            workItem.DocumentId, workItem.RunId, DocumentAIPipelines.Classification, includeFieldValues: true);

        await ApplyClassificationResultAsync(document, run, outcome, hasFigures, markedLength);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    /// <summary>
    /// External read (no UoW): the marked Markdown artifact (#371) for the document, or <c>null</c> when there is
    /// none (a non-figure document, or an archive that failed open) — the caller then classifies over the clean
    /// <c>Document.Markdown</c>. Fails open: a read error degrades to <c>null</c>, never faulting classification.
    /// </summary>
    private async Task<string?> TryReadMarkedMarkdownAsync(Guid documentId)
    {
        var blobName = DocumentConsts.MarkedMarkdownBlobPrefix + documentId;
        try
        {
            var bytes = await _blobContainer.GetAllBytesOrNullAsync(blobName);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to read marked Markdown blob {BlobName} for document {DocumentId}; classifying over the clean Document.Markdown.",
                blobName, documentId);
            return null;
        }
    }

    private async Task ApplyClassificationResultAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentClassificationOutcome outcome,
        bool hasFigures,
        int markedLength)
    {
        var isDerived = document.OriginDocumentId.HasValue;

        // #346 container branch: a parent that bundles several independent documents runs no field extraction
        // itself — mark it a container, complete the run as succeeded, do NOT publish DocumentClassifiedEto (so
        // FieldExtractionEventHandler never cascades — race-free suppression is simply never emitting the trigger),
        // and enqueue the unified sub-document detection pass to split it. MarkAsContainer leaves DocumentTypeId
        // null and sets no UnresolvedClassification reason, so the container is not sent to the review queue and —
        // with both key pipelines succeeded and no blocking reason — derives straight to Ready (Design A).
        //
        // Recursion guard (#346): a derived sub-document (OriginDocumentId set) must NOT be re-detected as a
        // container — that would recurse the split one level deeper. v1 bars recursion at depth one.
        if (outcome.IsContainer && !isDerived)
        {
            await PipelineRunManager.CompleteClassificationAsContainerAsync(document, run);

            // Enqueued in the SAME UoW as the completion so the container marker and the detection job commit
            // atomically via the outbox. The unified pass (#371) reads the marked Markdown and splits both text and
            // figure spans — there is no separate figure-routing job anymore.
            await _backgroundJobManager.EnqueueAsync(
                new DocumentSegmentationJobArgs { SourceDocumentId = document.Id });
            return;
        }

        if (outcome.IsContainer)
        {
            Logger.LogDebug(
                "Document {DocumentId} is a derived sub-document; ignoring the container signal (recursion guard) and classifying normally.",
                document.Id);
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
        }
        else
        {
            await PipelineRunManager.CompleteClassificationWithLowConfidenceAsync(
                document, run, outcome.Reason, outcome.Candidates);
        }

        // #371: a non-container parent may still embed a standalone document to route to its own sub-document. Enqueue
        // the unified pass when the classifier flagged it, OR — a fallback for a figure beyond the classification
        // truncation window — when the source is figure-bearing and its marked Markdown is longer than that window
        // (so the embedded-document signal may not have seen the tail figure). Runs IN ADDITION to the parent's own
        // extraction (DocumentClassifiedEto was published above for a confirmed type); the recursion guard prevents a
        // seeded sub-document (which carries no figures) from descending. Enqueued in this same UoW, so the job and
        // the run completion commit atomically via the outbox.
        var shouldRoute = !isDerived
            && (outcome.ContainsEmbeddedDocument
                || (hasFigures && markedLength > _aiOptions.MaxTextLengthPerExtraction));
        if (shouldRoute)
        {
            await _backgroundJobManager.EnqueueAsync(
                new DocumentSegmentationJobArgs { SourceDocumentId = document.Id });
        }
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

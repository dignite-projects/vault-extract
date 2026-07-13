using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.Classification;

[BackgroundJobName("VaultExtract.DocumentClassification")]
public class DocumentClassificationBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentClassificationJobArgs>, ITransientDependency
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly VaultExtractBehaviorOptions _aiOptions;
    private readonly ICurrentTenant _currentTenant;
    private readonly IBackgroundJobManager _backgroundJobManager;

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
        IOptions<VaultExtractBehaviorOptions> aiOptions,
        ICurrentTenant currentTenant,
        IBackgroundJobManager backgroundJobManager)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _documentTypeRepository = documentTypeRepository;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _aiOptions = aiOptions.Value;
        _currentTenant = currentTenant;
        _backgroundJobManager = backgroundJobManager;
    }

    public override async Task ExecuteAsync(DocumentClassificationJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            await ExecuteInTenantAsync(args);
        }
    }

    private async Task ExecuteInTenantAsync(DocumentClassificationJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        try
        {
            // #381: classify over Document.Markdown, which now retains the *[Image OCR]* figure provenance markers
            // (no separate marked artifact anymore). The embedded-document signal sees the figure spans directly, and
            // the figure-bearing recall trigger keys on ImageOcrMarkup.Contains over the same text.
            var markdown = workItem.Markdown;
            var outcome = await ClassifyAsync(workItem, markdown);
            await CompleteRunAsync(workItem, outcome, ImageOcrMarkup.Contains(markdown));
        }
        catch (Exception ex)
        {
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message, VaultExtractPipelines.Classification);
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
            document, args.PipelineRunId, VaultExtractPipelines.Classification);
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
        bool hasFigures)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        // includeFieldValues:true because the low-confidence path clears type-bound fields (#267
        // invariant), and EF needs the collection present to delete child rows.
        var (document, run) = await LoadDocumentAndRunAsync(
            workItem.DocumentId, workItem.RunId, VaultExtractPipelines.Classification, includeFieldValues: true);

        await ApplyClassificationResultAsync(document, run, outcome, hasFigures);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    private async Task ApplyClassificationResultAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentClassificationOutcome outcome,
        bool hasFigures)
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
            // atomically via the outbox. The unified pass (#371) reads Document.Markdown and splits both text and
            // figure spans — there is no separate figure-routing job anymore.
            await _backgroundJobManager.EnqueueAsync(
                new DocumentSegmentationJobArgs { SourceDocumentId = document.Id, TenantId = document.TenantId });
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

        // #371/#379: a non-container parent may still embed a standalone document to route to its own sub-document.
        // Enqueue the unified pass when the classifier flagged it, OR — deterministically — whenever the source is
        // figure-bearing (its Document.Markdown carries an *[Image OCR]* marker). Keying figure recall on the
        // already-computed, zero-cost ImageOcrMarkup.Contains signal (#379) makes the focused segmentation LLM the
        // single standalone-vs-element decision-maker, closing the within-window hole where a multi-objective
        // classifier under-flagged an embedded figure (ContainsEmbeddedDocument=false) on a doc whose Document.Markdown
        // fit inside the classification truncation window — previously the figure arm fired only beyond that window,
        // so an in-window embedded document was never routed and never entered review (a silent recall miss).
        // No structural pre-check guards the figure arm: this trigger is only reached for a non-container parent, where
        // the pass routes ONLY figure spans, so "has a candidate span" is exactly "Contains a figure" — a pre-check
        // would be vacuous, and any content-length variant that skipped present figures would reopen the recall hole.
        // A decorative-only figure is a clean no-op in the pass (logged, marked segmented, no review flag), and the
        // per-doc IsSegmented gate means each figure-bearing document pays the segmentation LLM at most once.
        // Runs IN ADDITION to the parent's own extraction (DocumentClassifiedEto was published above for a confirmed
        // type); the recursion guard prevents a seeded sub-document (which carries no figures) from descending.
        // Enqueued in this same UoW, so the job and the run completion commit atomically via the outbox.
        var shouldRoute = !isDerived && (outcome.ContainsEmbeddedDocument || hasFigures);
        if (shouldRoute)
        {
            await _backgroundJobManager.EnqueueAsync(
                new DocumentSegmentationJobArgs { SourceDocumentId = document.Id, TenantId = document.TenantId });
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
    public Guid? TenantId { get; set; }
    public Guid? PipelineRunId { get; set; }
}

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// <c>field-extraction</c> pipeline background job (#289 step 2): execution unit for on-demand / bulk
/// field re-extraction. Reuses <see cref="DocumentPipelineBackgroundJobBase{TArgs}"/> for the
/// three-stage short UoW pattern plus <see cref="DocumentPipelineRun"/> observability / failure retry,
/// while external LLM extraction delegates to the shared #289 step 1 engine
/// <see cref="FieldExtractionService"/>.
/// <para>
/// <b>Lifecycle-affecting since #411</b>: <see cref="ExtractPipelines.FieldExtraction"/> is now a key pipeline
/// (so the duplicate check can gate Ready). <c>DeriveLifecycleAsync</c> triggered by BeginRun / CompleteRun
/// therefore participates in the Ready gate: the first run releases the document to Ready, and a re-extraction of
/// an already-Ready document bounces it <c>Ready -&gt; Processing -&gt; Ready</c>, re-firing <c>DocumentReadyEto</c>
/// (downstream absorbs the re-delivery via <c>EventTime</c> idempotency). A run that detects a duplicate leaves the
/// document blocked (DuplicateSuspected) in the operator review queue.
/// </para>
/// <para>
/// Same three stages as the classification job: BeginRun (short UoW creates / resumes run and marks
/// Running), external LLM extraction (no UoW), then CompleteRun (short UoW marks Succeeded). Any
/// exception calls <see cref="DocumentPipelineBackgroundJobBase{TArgs}.FailRunAsync"/> to mark Failed,
/// then rethrows to trigger ABP background job retry.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DocumentFieldExtraction")]
public class DocumentFieldExtractionBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentFieldExtractionJobArgs>, ITransientDependency
{
    private readonly FieldExtractionService _fieldExtractionService;

    public DocumentFieldExtractionBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        FieldExtractionService fieldExtractionService)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _fieldExtractionService = fieldExtractionService;
    }

    public override async Task ExecuteAsync(DocumentFieldExtractionJobArgs args)
    {
        var (documentId, runId, tenantId) = await BeginRunAsync(args);

        try
        {
            // External LLM extraction. The engine internally performs ICurrentTenant.Change(tenantId)
            // + its own three-stage short UoW flow and is never called inside any existing UoW.
            // #411: ExpectedEventTypeCode is the cascade's stale-reclassify-event early-exit hint (null on the
            // on-demand / bulk path, which always extracts the current type).
            await _fieldExtractionService.ExtractAsync(documentId, tenantId, args.ExpectedEventTypeCode);
            await CompleteRunAsync(documentId, runId);
        }
        catch (Exception ex)
        {
            await FailRunAsync(documentId, runId, ex.Message, ExtractPipelines.FieldExtraction);
            throw;
        }
    }

    private async Task<(Guid DocumentId, Guid RunId, Guid? TenantId)> BeginRunAsync(DocumentFieldExtractionJobArgs args)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var document = await DocumentRepository.GetAsync(args.DocumentId, includeDetails: false);
        var run = await PipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, ExtractPipelines.FieldExtraction);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return (document.Id, run.Id, document.TenantId);
    }

    private async Task CompleteRunAsync(Guid documentId, Guid runId)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var (document, run) = await LoadDocumentAndRunAsync(documentId, runId, ExtractPipelines.FieldExtraction);
        await PipelineRunManager.CompleteAsync(document, run);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }
}

public class DocumentFieldExtractionJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }

    /// <summary>
    /// #411: forwarded by the classification cascade (<see cref="FieldExtractionEventHandler"/>) as the
    /// stale-reclassify-event early-exit hint. <c>null</c> on the on-demand / bulk (#289) path, which always
    /// extracts against the document's current type.
    /// </summary>
    public string? ExpectedEventTypeCode { get; set; }
}

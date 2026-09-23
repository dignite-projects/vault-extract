using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Shared skeleton for document pipeline background jobs (text extraction / classification,
/// #216 follow-up #2). Encapsulates repeated Complete/Fail stage setup for both jobs: reload Document
/// inside a short UoW, locate PipelineRun with runId fallback reconstruction, and perform the
/// identical failure closeout. Begin-stage logic differs by job (candidate assembly / blob read), so
/// it stays outside the base class.
/// <para>
/// The three-stage short-UoW discipline is documented in <c>.claude/rules/background-jobs.md</c>:
/// Begin / Complete / Fail each use an independent UoW, and slow external work (OCR / LLM / blob IO)
/// runs outside any UoW. Since #216, <see cref="DocumentPipelineRun"/> is an independent aggregate
/// root read and written directly through <see cref="IDocumentPipelineRunRepository"/> instead of via
/// the <see cref="Document"/> aggregate.
/// </para>
/// </summary>
public abstract class DocumentPipelineBackgroundJobBase<TArgs> : AsyncBackgroundJob<TArgs>
{
    protected IDocumentRepository DocumentRepository { get; }
    protected IDocumentPipelineRunRepository RunRepository { get; }
    protected DocumentPipelineRunManager PipelineRunManager { get; }
    protected DocumentPipelineRunAccessor PipelineRunAccessor { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IDataFilter DataFilter { get; }

    /// <summary>
    /// #662: the status message of a run ended because its document was deleted while the job waited. Operators see
    /// it in the pipeline-run list; after a restore the run is <c>Failed</c> and retryable.
    /// </summary>
    public const string DocumentDeletedRunMessage = "The document was deleted before this run could complete.";

    protected DocumentPipelineBackgroundJobBase(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        IDataFilter dataFilter)
    {
        DocumentRepository = documentRepository;
        RunRepository = runRepository;
        PipelineRunManager = pipelineRunManager;
        PipelineRunAccessor = pipelineRunAccessor;
        UnitOfWorkManager = unitOfWorkManager;
        DataFilter = dataFilter;
    }

    /// <summary>
    /// #662: whether a Begin phase failed because this job's document no longer resolves — it was soft-deleted (an
    /// operator delete, or a sub-document withdrawn by a re-parse / container→concrete reclassify) or permanently
    /// deleted while the job waited in the queue. Each job's Begin loads the document with <c>GetAsync</c>, so this is
    /// the <see cref="EntityNotFoundException"/> it throws for <see cref="Document"/>.
    /// </summary>
    protected static bool IsDocumentGone(EntityNotFoundException exception)
        => exception.EntityType == typeof(Document);

    /// <summary>
    /// #662: ends a job whose document is gone, instead of rethrowing into ABP's retry loop. Rethrowing used to retry
    /// the job with backoff until ABP abandoned it (about 12 attempts over two days), and a document restored after
    /// that kept a <c>Pending</c> run nothing would execute: stuck in Processing, and <c>RetryPipelineAsync</c> refuses
    /// a run that is not <c>Failed</c>.
    /// <para>
    /// A soft-deleted document's run — <c>Pending</c> if the job never began, <c>Running</c> if it was deleted mid-run and
    /// the job is being retried — is marked <c>Failed</c> with <see cref="DocumentDeletedRunMessage"/>, and the lifecycle
    /// is re-derived with the soft-delete filter off. If the document is restored, it comes back Failed with a run the
    /// operator can retry. A permanently deleted document has nothing left to record, so the job just ends. Nothing is
    /// published: the run-completed event is local and has no handler, and a Failed transition fires no egress event.
    /// </para>
    /// </summary>
    protected virtual async Task EndForDeletedDocumentAsync(Guid documentId, Guid? pipelineRunId, string pipelineCode)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        using (DataFilter.Disable<ISoftDelete>())
        {
            var document = await DocumentRepository.FindAsync(documentId, includeDetails: false);
            if (document == null)
            {
                Logger.LogInformation(
                    "Document {DocumentId} was permanently deleted before its queued {PipelineCode} job ran; ending the job.",
                    documentId, pipelineCode);
                await uow.CompleteAsync();
                return;
            }

            var run = pipelineRunId.HasValue ? await RunRepository.FindAsync(pipelineRunId.Value) : null;
            if (run == null || run.PipelineCode != pipelineCode)
            {
                run = await RunRepository.FindLatestByDocumentAndCodeAsync(documentId, pipelineCode);
            }

            if (run is { Status: PipelineRunStatus.Pending or PipelineRunStatus.Running })
            {
                await PipelineRunManager.FailAsync(document, run, DocumentDeletedRunMessage);
                await DocumentRepository.UpdateAsync(document, autoSave: true);
            }

            Logger.LogInformation(
                "Document {DocumentId} was deleted before its queued {PipelineCode} job ran; ended the job and failed its run (#662).",
                documentId, pipelineCode);
        }

        await uow.CompleteAsync();
    }

    /// <summary>
    /// Shared loading for the Complete / Fail stages: fetch Document without eager-loading runs,
    /// locate this job's run by <paramref name="runId"/>, and when missing reconstruct it with that
    /// runId through <see cref="DocumentPipelineRunAccessor.BeginOrStartAsync"/> as fallback. Callers
    /// are responsible for invoking and committing this inside their own short UoW.
    /// <para>
    /// When <paramref name="includeFieldValues"/> is true, eager-load
    /// <see cref="Document.FieldValidationWarnings"/>. Only the classification job's Complete stage needs
    /// this: the low-confidence path calls <c>RequestClassificationReview</c>, which clears
    /// type-bound fields (#267) and their validation warnings, and EF needs the warnings collection
    /// present to actually delete its child rows. Text extraction / failure closeout paths use the
    /// default false to avoid unnecessary JOINs.
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
    /// Failure closeout: in an independent short UoW, reload Document + run, mark the run failed
    /// (Manager derives LifecycleStatus from this internally), persist the Document main row, and
    /// commit. The failure paths for the two jobs are identical except for
    /// <paramref name="pipelineCode"/>, so this is lifted to the base class. Callers typically
    /// rethrow after calling this from catch so ABP background job retry is triggered.
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

using System;
using System.Threading.Tasks;
using Volo.Abp.BackgroundJobs;
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
    /// Shared loading for the Complete / Fail stages: fetch Document without eager-loading runs,
    /// locate this job's run by <paramref name="runId"/>, and when missing reconstruct it with that
    /// runId through <see cref="DocumentPipelineRunAccessor.BeginOrStartAsync"/> as fallback. Callers
    /// are responsible for invoking and committing this inside their own short UoW.
    /// <para>
    /// When <paramref name="includeFieldValues"/> is true, eager-load
    /// <see cref="Document.ExtractedFieldValues"/>. Only the classification job's Complete stage needs
    /// this: the low-confidence path calls <c>RequestClassificationReview</c>, which clears
    /// type-bound fields (#267), and EF needs the collection present to actually delete child rows.
    /// Text extraction / failure closeout paths use the default false to avoid unnecessary JOINs.
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

using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Duplicates;

/// <summary>
/// Brings a document type's persisted <see cref="DocumentReviewReasons.DuplicateSuspected"/> bits back in line
/// with its <see cref="DocumentType.DuplicateScope"/> after an admin changes it (#651 §5).
/// <para>
/// The bit is not a computed view: <c>Document.ReviewReasons</c> is a persisted, required column written by the
/// field-extraction write phase, so changing the setting changes no existing verdict by itself. <b>Both switch
/// directions leave a stale half</b>, because the stored bit records <i>that</i> something collided, never
/// <i>who</i>:
/// </para>
/// <list type="bullet">
///   <item><b>Layer → Uploader</b> (narrowing): an unflagged document collided with nothing layer-wide, so it
///   cannot collide in a subset — but a flagged one may have collided with another uploader's copy, and left
///   alone it becomes a false park. The document sits in the review queue, the detail panel now returns nothing
///   for it, and the only control the operator has left — <c>AllowDuplicateAsync</c> — would durably suppress
///   every future real duplicate.</item>
///   <item><b>Uploader → Layer</b> (widening): a flagged document collided within one uploader, so it still
///   collides layer-wide — but an unflagged one may now collide with another uploader's copy, and left alone
///   that is silent under-detection.</item>
/// </list>
/// <para>
/// <b>Flag-only.</b> No LLM call, no field value touched, no <c>DocumentReadyEto</c>-triggering re-extraction and
/// no <c>FieldsExtractedEto</c>. That is what separates it from #289 bulk reprocessing, whose "human owns the
/// judgment, configuration has zero cascade" rule exists because re-extraction spends an LLM call per document
/// and overwrites manual corrections. This belongs to the #528 family — derived state follows the schema that
/// produced it — and like #528 it is enqueued automatically on save rather than offered as a second button:
/// between the save and that button the system would be stating a verdict it no longer believes.
/// </para>
/// <para>
/// Shape (§5 algorithm): per batch, one narrow keyset-paged projection of the type's documents, then <b>one
/// aggregate restricted to that page's fingerprints</b> (which of its keys still collide under the
/// <i>current</i> scope), the diff in memory, and a load + write for <b>only</b> the documents whose verdict
/// changes. The naive "page ids, load each row, re-query" shape — what <c>DuplicateBasisCleanupJob</c> does —
/// would load every row of the type including <c>Markdown</c>, and almost all of those loads would write nothing
/// back.
/// </para>
/// <para>
/// <b>The aggregate is bounded by the page, not by the type.</b> The issue's §5 sketches step 1 as one aggregate
/// over the whole type; taken literally that is a full-type aggregate <i>per chained batch</i> — N/batch of them
/// over a run, quadratic in the type's size. At 10,000 documents it is unnoticeable; at 100,000 it is minutes of
/// database time, and the entire case for reconciling automatically on save rather than behind a button is that
/// it is cheap. Restricting the aggregate to the fingerprints actually on the page keeps each one O(batch) and
/// the run linear, and costs nothing in correctness: a row's verdict depends only on its own bucket.
/// </para>
/// <para>
/// Tenant-scoped, idempotent, retry-safe and chained in bounded batches, on the same terms as #528. The write
/// goes through the entity rather than one set-based <c>UPDATE</c> because lifecycle re-derivation, audit and the
/// ABP conventions all live there.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DuplicateScopeReconciliation")]
public class DuplicateScopeReconciliationJob
    : AsyncBackgroundJob<DuplicateScopeReconciliationArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    /// <summary>
    /// The verdict itself, shared with the type form's pre-save preview (#651 §5). The preview quotes what this
    /// job will change, so the two must compute it the same way; the only way that cannot drift is for there to
    /// be one implementation.
    /// </summary>
    private readonly DuplicateScopeVerdictCalculator _verdictCalculator;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDataFilter _dataFilter;

    public DuplicateScopeReconciliationJob(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        DuplicateScopeVerdictCalculator verdictCalculator,
        DocumentPipelineRunManager pipelineRunManager,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IDataFilter dataFilter)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _verdictCalculator = verdictCalculator;
        _pipelineRunManager = pipelineRunManager;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _dataFilter = dataFilter;
    }

    public override async Task ExecuteAsync(DuplicateScopeReconciliationArgs args)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        using (_currentTenant.Change(args.TenantId))
        {
            int rowCount;
            var changed = 0;

            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                // The setting is read HERE, at execution time, and never carried in the args. Two switches in
                // quick succession then converge on the final setting instead of racing — the same reason the
                // #528 job rechecks the live schema before acting — and every chained batch re-reads it, so a
                // third switch mid-run takes effect from the next batch rather than finishing under a rule the
                // admin has already retracted.
                var documentType = await _documentTypeRepository.FindAsync(args.DocumentTypeId, includeDetails: false);
                if (documentType == null)
                {
                    await uow.CompleteAsync();
                    Logger.LogInformation(
                        "Duplicate scope reconciliation skipped: DocumentType {DocumentTypeId} no longer exists in tenant {TenantId}.",
                        args.DocumentTypeId, args.TenantId);
                    return;
                }

                var detectionScope = documentType.DuplicateScope;

                // Step 1: the narrow projection, recycle-bin rows INCLUDED — restoring a document must not bring
                // back a flag derived from a rule that no longer exists.
                var rows = await _documentRepository.GetDuplicateReconciliationPageAsync(
                    args.DocumentTypeId, args.AfterId, batchSize);
                rowCount = rows.Count;

                // Steps 2 and 3: one aggregate bounded by this page's fingerprints, then the diff in memory.
                // Both live in DuplicateScopeVerdictCalculator because the type form's pre-save preview counts
                // exactly these verdicts — a preview computed by a different rule is not a preview of this job.
                // Rows already carrying the verdict their new scope justifies never come back from it, so the
                // loaded set below is the actual difference and nothing else; DuplicateAllowed rows never come
                // back either, because an operator decided and reconciliation may neither re-litigate that nor
                // forge one (nothing here may call Document.AllowDuplicate — the flag is only ever cleared
                // through SetReviewReason, the same line #528 draws).
                var changes = await _verdictCalculator.ComputeChangesAsync(
                    args.DocumentTypeId, detectionScope, rows);

                foreach (var (row, target) in changes)
                {
                    // Step 4: load and write only the difference, through the domain object. Recycle-bin rows are
                    // in scope, hence the disabled ISoftDelete — and the load deliberately goes through
                    // FindWithFieldValuesAsync rather than FindAsync(id, includeDetails: false): the latter is
                    // ABP's DbSet.FindAsync, a primary-key lookup that bypasses global query filters entirely, so
                    // it would also bypass IMultiTenant. Every other row load on this path keeps the tenant
                    // boundary a filter rather than a convention, and the #528 job loads the same way.
                    using (_dataFilter.Disable<ISoftDelete>())
                    {
                        var document = await _documentRepository.FindWithFieldValuesAsync(row.Id);
                        if (document == null)
                        {
                            continue;
                        }

                        document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, target);

                        // A recycle-bin document has no live lifecycle to re-derive; its flag is corrected in
                        // place so that restoring it later lands on the right verdict.
                        if (!document.IsDeleted)
                        {
                            await _pipelineRunManager.ReDeriveLifecycleAsync(document);
                        }

                        await _documentRepository.UpdateAsync(document);
                        changed++;
                    }
                }

                if (rowCount == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new DuplicateScopeReconciliationArgs
                        {
                            DocumentTypeId = args.DocumentTypeId,
                            TenantId = args.TenantId,
                            AfterId = rows[^1].Id
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Duplicate scope reconciliation: {Changed} of {Scanned} document(s) re-flagged for type {DocumentTypeId} (afterId={AfterId}, continued={Continued}).",
                changed, rowCount, args.DocumentTypeId, args.AfterId, rowCount == batchSize);
        }
    }
}

public class DuplicateScopeReconciliationArgs
{
    public Guid DocumentTypeId { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: only documents with <c>Id &gt; AfterId</c>; null for the first batch.</summary>
    public Guid? AfterId { get; set; }
}

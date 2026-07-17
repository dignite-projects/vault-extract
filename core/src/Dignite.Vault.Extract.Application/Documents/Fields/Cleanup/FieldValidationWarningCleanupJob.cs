using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Fields.Cleanup;

/// <summary>
/// Removes the now-orphaned field validation warnings left behind when a <see cref="FieldDefinition"/> is deleted
/// (#528). A warning naming a field that no longer exists keeps the blocking
/// <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit set, so the document stays parked out of
/// <c>DocumentReadyEto</c> until an operator resolves it by hand or a re-extraction happens to run — silently
/// withheld from every downstream consumer in the meantime.
/// <para>
/// A job rather than inline work in <c>FieldDefinitionAppService.DeleteAsync</c>: the affected set is bounded only by
/// the type's document count, so the delete request must not carry it. It chains itself forward one bounded batch at a
/// time (the #289 dispatcher pattern), keeping job granularity short. Enqueued inside the delete's UoW, so it never
/// runs for a delete that rolled back.
/// </para>
/// <para>
/// Idempotent and retry-safe: cleaned documents drop out of the scan predicate, so a re-run of the same batch finds
/// nothing and the chain still makes forward progress through the keyset cursor. At-least-once chaining may re-run a
/// batch after a crash; that is a no-op here, not a double-apply.
/// </para>
/// <para>
/// Deliberately publishes no <c>FieldsExtractedEto</c>: field <b>values</b> were not re-extracted and remain
/// historical data — only the warning derived from a deleted schema element goes away (#528 acceptance criteria).
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.FieldValidationWarningCleanup")]
public class FieldValidationWarningCleanupJob
    : AsyncBackgroundJob<FieldValidationWarningCleanupArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDataFilter _dataFilter;

    public FieldValidationWarningCleanupJob(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IDataFilter dataFilter)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _dataFilter = dataFilter;
    }

    public override async Task ExecuteAsync(FieldValidationWarningCleanupArgs args)
    {
        // Same bounded granularity as the #289 reprocessing dispatch: this is the same "one page of documents per
        // job run" trade, and a second knob for it would only be a second thing to tune wrong.
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        // Background workers do not restore the tenant context on their own; the scans below are isolated by the
        // ambient IMultiTenant filter, so it must be the field definition's own layer.
        using (_currentTenant.Change(args.TenantId))
        {
            List<Guid> ids;

            // One short UoW per batch: the document writes and the next chained job commit together, so the chain
            // cannot survive a rolled-back batch. No external work happens here (pure DB), so the three-phase split
            // background-jobs.md requires for OCR/LLM jobs does not apply.
            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                ids = await _documentRepository.GetIdsWithFieldValidationWarningAsync(
                    args.FieldDefinitionId, args.AfterId, batchSize);

                foreach (var id in ids)
                {
                    // Recycle-bin documents are in scope (see the repository contract), so both the load and the
                    // write run with ISoftDelete disabled — otherwise the scan would hand back an id the load then
                    // resolves to null.
                    using (_dataFilter.Disable<ISoftDelete>())
                    {
                        // FindWithFieldValuesAsync, not the lean load: removing a warning must delete the persisted
                        // child row, not merely clear the bit (#527 load-path contract).
                        var document = await _documentRepository.FindWithFieldValuesAsync(id);
                        if (document == null)
                        {
                            continue;
                        }

                        // The aggregate keeps the collection and the blocking bit coupled: the bit clears only when
                        // no warning remains, so a document warned on another field stays parked (#527 §9).
                        document.ResolveFieldValidationWarnings(new List<Guid> { args.FieldDefinitionId });

                        // Only a live document has a lifecycle to re-derive; a recycle-bin document is cleaned so
                        // that restoring it later cannot resurrect review state for a field that no longer exists,
                        // and its own restore re-derives from there.
                        if (!document.IsDeleted)
                        {
                            await _pipelineRunManager.ReDeriveLifecycleAsync(document);
                        }

                        await _documentRepository.UpdateAsync(document);
                    }
                }

                // A full batch means there may be more: chain forward with the last Id as the keyset cursor. A
                // partial batch means the range is exhausted.
                if (ids.Count == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new FieldValidationWarningCleanupArgs
                        {
                            FieldDefinitionId = args.FieldDefinitionId,
                            TenantId = args.TenantId,
                            AfterId = ids[^1]
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Field validation warning cleanup: cleared {Count} document(s) for deleted field {FieldDefinitionId} (afterId={AfterId}, continued={Continued}).",
                ids.Count, args.FieldDefinitionId, args.AfterId, ids.Count == batchSize);
        }
    }
}

public class FieldValidationWarningCleanupArgs
{
    public Guid FieldDefinitionId { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: only documents with <c>Id &gt; AfterId</c>; null for the first batch.</summary>
    public Guid? AfterId { get; set; }
}

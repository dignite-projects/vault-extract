using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.Reprocessing;

/// <summary>
/// Dispatcher job for bulk field re-extraction (#289 step 4). It chains itself forward so the system
/// keeps only one short job granularity. Each run handles one batch: keyset-paginate IDs only
/// (<c>WHERE Id &gt; lastId ORDER BY Id Take(N)</c>, <c>AsNoTracking + Select(Id)</c>), enqueue one
/// <see cref="DocumentFieldExtractionBackgroundJob"/> per document in the batch, enqueue the next
/// dispatcher with a cursor when the batch is full, then finish.
/// <para>
/// Scope is fixed by <see cref="DocumentFieldReextractionDispatcherArgs.DocumentTypeId"/> because
/// field values are meaningless outside their type. Manually confirmed documents are not excluded:
/// field re-extraction does not change classification and only re-extracts field values, so
/// overwriting manual field corrections is an accepted light cost.
/// </para>
/// <para>
/// Honest cost (#289): at-least-once chained reruns can fork. If a dispatcher crashes after commit but
/// before being marked successful, rerun will enqueue the same batch plus the next dispatcher again.
/// Results remain correct because per-document <c>SetFields</c> replaces the whole group idempotently;
/// the worst case is extra cost, which is accepted.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DocumentFieldReextractionDispatcher")]
public class DocumentFieldReextractionDispatcherJob
    : AsyncBackgroundJob<DocumentFieldReextractionDispatcherArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentFieldReextractionDispatcherJob(
        IDocumentRepository documentRepository,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public override async Task ExecuteAsync(DocumentFieldReextractionDispatcherArgs args)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        // Explicitly restore the target tenant context. Background workers do not necessarily restore
        // it automatically; the ID range query is isolated through the ambient IMultiTenant filter.
        using (_currentTenant.Change(args.TenantId))
        {
            List<Guid> ids;

            // One short UoW per batch: read IDs, enqueue per-document jobs, and enqueue the next
            // dispatcher as one atomic commit.
            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                // args.DocumentTypeId layer ownership was already validated by
                // DocumentReprocessingAppService.EnsureTypeInCurrentLayerAsync before enqueueing; here
                // we trust args produced by the authorized AppService. Even if a cross-layer type is
                // passed in, the ambient IMultiTenant filter makes GetIdsForReprocessingAsync hit zero
                // rows, failing closed without leakage.
                ids = await _documentRepository.GetIdsForReprocessingAsync(
                    documentTypeId: args.DocumentTypeId,
                    withReason: null,
                    excludeManuallyConfirmed: false,
                    afterId: args.AfterId,
                    maxCount: batchSize);

                foreach (var id in ids)
                {
                    // Bulk path intentionally skips EnsureNotInProgress, unlike single-document
                    // ReextractFieldsAsync. Concurrent bulk + single-document re-extraction may run
                    // two field-extraction runs for the same document, but FieldExtractionService
                    // SetFields replaces the whole group idempotently. Concurrent writes to the same
                    // document make the loser hit Document optimistic concurrency
                    // AbpDbConcurrencyException, mark the run Failed, and let ABP retry cleanly. Final
                    // state is consistent; the worst case is one extra LLM cost, the same accepted cost
                    // as chained dispatcher forking in this file.
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentFieldExtractionJobArgs { DocumentId = id });
                }

                // Full batch means there may be a next page: cursor = last ID in this batch and chain
                // the next dispatcher. A partial batch means the range is exhausted.
                if (ids.Count == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentFieldReextractionDispatcherArgs
                        {
                            DocumentTypeId = args.DocumentTypeId,
                            TenantId = args.TenantId,
                            AfterId = ids[^1]
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Field re-extraction dispatcher: enqueued {Count} document(s) for type {DocumentTypeId} (afterId={AfterId}, continued={Continued}).",
                ids.Count, args.DocumentTypeId, args.AfterId, ids.Count == batchSize);
        }
    }
}

public class DocumentFieldReextractionDispatcherArgs
{
    public Guid DocumentTypeId { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: enumerate only documents where <c>Id &gt; AfterId</c>; null for the first batch.</summary>
    public Guid? AfterId { get; set; }
}

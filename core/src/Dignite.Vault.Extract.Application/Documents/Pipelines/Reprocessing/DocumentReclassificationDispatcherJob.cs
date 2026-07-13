using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.Reprocessing;

/// <summary>
/// Dispatcher job for bulk reclassification (#289 step 5). It uses the same chained self-continuing
/// base shape as the field re-extraction dispatcher, but runs the <c>classification</c> pipeline
/// (success necessarily cascades field re-extraction) and applies scope conditions (type /
/// pending-review queue / protect manual confirmation). Each batch keyset-paginates IDs only,
/// enqueues one <see cref="DocumentClassificationBackgroundJob"/> per document
/// (<c>PipelineRunId=null</c>, so the job calls StartAsync to create a new classification run),
/// enqueues the next dispatcher with a cursor when the batch is full, then finishes.
/// <para>
/// Destructive behavior (#289 scenario-one asymmetry): reclassification overwrites automatic
/// classification, sends low-confidence documents back to review, and clears fields. Scope / manual
/// confirmation protection is encoded into the ID range query through
/// <see cref="DocumentReclassificationDispatcherArgs"/>. The dispatcher reads only IDs and does not
/// make per-document decisions here.
/// </para>
/// <para>
/// Lifecycle: classification is a key pipeline, so reclassification temporarily moves already Ready
/// documents back to Processing, matching single-document re-recognition (#263). Completion re-derives
/// state from confidence: passing returns to Ready; failing goes to manual review.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DocumentReclassificationDispatcher")]
public class DocumentReclassificationDispatcherJob
    : AsyncBackgroundJob<DocumentReclassificationDispatcherArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentReclassificationDispatcherJob(
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

    public override async Task ExecuteAsync(DocumentReclassificationDispatcherArgs args)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        using (_currentTenant.Change(args.TenantId))
        {
            List<Guid> ids;

            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                ids = await _documentRepository.GetIdsForReprocessingAsync(
                    documentTypeId: args.DocumentTypeId,
                    withReason: args.WithReason,
                    excludeManuallyConfirmed: args.ExcludeManuallyConfirmed,
                    afterId: args.AfterId,
                    maxCount: batchSize);

                foreach (var id in ids)
                {
                    // PipelineRunId=null makes the job's BeginOrStartAsync call StartAsync and create
                    // a new classification attempt.
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentClassificationJobArgs { DocumentId = id, TenantId = args.TenantId });
                }

                if (ids.Count == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentReclassificationDispatcherArgs
                        {
                            DocumentTypeId = args.DocumentTypeId,
                            WithReason = args.WithReason,
                            ExcludeManuallyConfirmed = args.ExcludeManuallyConfirmed,
                            TenantId = args.TenantId,
                            AfterId = ids[^1]
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Reclassification dispatcher: enqueued {Count} document(s) (type={DocumentTypeId}, withReason={WithReason}, excludeConfirmed={ExcludeConfirmed}, afterId={AfterId}, continued={Continued}).",
                ids.Count, args.DocumentTypeId, args.WithReason, args.ExcludeManuallyConfirmed, args.AfterId, ids.Count == batchSize);
        }
    }
}

public class DocumentReclassificationDispatcherArgs
{
    /// <summary>Non-null means only this type (<see cref="ReclassificationScope.OnlyCurrentType"/>); null means all / cross-type.</summary>
    public Guid? DocumentTypeId { get; set; }

    /// <summary>Non-null means only documents containing this review reason. The pending-review queue scope passes <see cref="DocumentReviewReasons.UnresolvedClassification"/> (#284 two-axis model).</summary>
    public DocumentReviewReasons? WithReason { get; set; }

    /// <summary>true excludes manually confirmed documents (<see cref="DocumentReviewDisposition.Confirmed"/>), protecting manual confirmation.</summary>
    public bool ExcludeManuallyConfirmed { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: enumerate only documents where <c>Id &gt; AfterId</c>; null for the first batch.</summary>
    public Guid? AfterId { get; set; }
}

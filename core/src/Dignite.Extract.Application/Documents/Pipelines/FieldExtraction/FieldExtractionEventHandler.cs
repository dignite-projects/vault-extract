using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction EventHandler (field architecture v2). Subscribes to
/// <see cref="DocumentClassifiedEto"/> and cascades field extraction after classification completes.
/// <para>
/// Since #411 the cascade <b>enqueues <see cref="DocumentFieldExtractionBackgroundJob"/></b> rather than calling
/// <see cref="FieldExtractionService"/> inline. The reason is the Ready gate: <c>field-extraction</c> became a key
/// pipeline (so the duplicate check can withhold <c>DocumentReadyEto</c>), which means the cascade path must also
/// create a <c>DocumentPipelineRun</c> for field extraction — and the run-managed three-stage job
/// (BeginRun → external LLM extraction → CompleteRun → DeriveLifecycle) is exactly where that run is created and
/// where Ready is re-derived once extraction succeeds. Routing through the job unifies the cascade and the
/// on-demand / bulk (#289) trigger on one execution path. The stale-reclassify-event hint (the event's TypeCode)
/// is forwarded via <see cref="DocumentFieldExtractionJobArgs.ExpectedEventTypeCode"/> so the engine keeps its
/// early-exit optimization.
/// </para>
/// </summary>
public class FieldExtractionEventHandler
    : IDistributedEventHandler<DocumentClassifiedEto>, ITransientDependency
{
    private readonly IBackgroundJobManager _backgroundJobManager;

    public FieldExtractionEventHandler(IBackgroundJobManager backgroundJobManager)
    {
        _backgroundJobManager = backgroundJobManager;
    }

    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        if (string.IsNullOrWhiteSpace(eventData.DocumentTypeCode))
        {
            return;
        }

        // Enqueued in the ambient event-handler UoW so the job persists transactionally with event consumption.
        // The engine extracts by the Document's current DocumentTypeId (#207); ExpectedEventTypeCode is only the
        // stale-reclassify-event early-exit hint.
        await _backgroundJobManager.EnqueueAsync(
            new DocumentFieldExtractionJobArgs
            {
                DocumentId = eventData.DocumentId,
                ExpectedEventTypeCode = eventData.DocumentTypeCode
            });
    }
}

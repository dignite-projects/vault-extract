using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.Vault.Extract.Documents.Pipelines.Lifecycle;

/// <summary>
/// Listens to <see cref="DocumentLifecycleStatusChangedEvent"/> and publishes
/// <see cref="DocumentReadyEto"/> when a document transitions to
/// <see cref="DocumentLifecycleStatus.Ready"/>. This is the trusted signal downstream consumers
/// subscribe to by default under CLAUDE.md "Outbound Event Contract".
/// <para>
/// The Ready gate is enforced by the classification stage: documents with insufficient automatic
/// classification confidence / no suitable type receive the blocking UnresolvedClassification reason
/// and enter the manual review queue, which keeps them out of Ready. Therefore this handler needs no
/// extra check: <c>NewStatus == Ready</c> implicitly means the gate passed. #346: a <b>container</b> also
/// reaches Ready lifecycle but has <b>no</b> confirmed type, so it is <b>not</b> a consumable document — this
/// handler suppresses its <c>DocumentReadyEto</c>; downstream consumes only the container's sub-documents,
/// each emitting its own Ready event. This keeps the contract "<c>DocumentReadyEto</c> ⟺ a confirmed type".
/// </para>
/// </summary>
public class DocumentReadyEventHandler
    : ILocalEventHandler<DocumentLifecycleStatusChangedEvent>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly ILogger<DocumentReadyEventHandler> _logger;

    public DocumentReadyEventHandler(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ILogger<DocumentReadyEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(DocumentLifecycleStatusChangedEvent eventData)
    {
        if (eventData.NewStatus != DocumentLifecycleStatus.Ready)
        {
            return;
        }

        var document = await _documentRepository.FindAsync(eventData.DocumentId, includeDetails: false);
        if (document == null)
        {
            _logger.LogWarning(
                "DocumentLifecycleStatusChangedEvent for missing document {DocumentId} — DocumentReadyEto not published.",
                eventData.DocumentId);
            return;
        }

        // #527 §8: defensive current-state re-check. The DocumentReadyEto publish is driven by a lifecycle-changed
        // event, but by the time this handler runs the document may have moved on — a fast reclassification (manual or
        // automatic) queues a new pending field-extraction run and derives the document back to Processing /
        // PendingReview (or an operator rejection derives Failed). Re-reading the committed state and requiring it to
        // still be Ready stops a stale / redelivered transition from releasing a document that is no longer Ready. The
        // internal cascade is scheduled transactionally with classification since #527 §8, so this is defense-in-depth,
        // not the primary gate.
        if (document.LifecycleStatus != DocumentLifecycleStatus.Ready)
        {
            _logger.LogInformation(
                "Document {DocumentId} is no longer Ready (now {LifecycleStatus}) when handling a stale Ready transition; suppressing DocumentReadyEto.",
                document.Id, document.LifecycleStatus);
            return;
        }

        // #346: a container has no confirmed type, so it is NOT a consumable business document — it does not emit the
        // type-confirmed DocumentReadyEto. (Doing so would be a type-less "ready" fired before its sub-documents even
        // exist, with no later way to signal a segmentation failure — the contract break Codex's adversarial review
        // flagged.) The container still reaches Ready *lifecycle* for the operator UI; downstream consumes only the
        // sub-documents' own DocumentReadyEto, each carrying OriginDocumentId back to this container. A container the
        // segmenter cannot split surfaces in the operator review queue (SegmentationIncomplete), exactly like a
        // low-confidence document waits in review without emitting Ready. This keeps the documented contract
        // "DocumentReadyEto ⟺ a confirmed type" intact.
        if (document.IsContainer)
        {
            _logger.LogInformation(
                "Document {DocumentId} reached Ready lifecycle as a container; suppressing DocumentReadyEto (its sub-documents emit their own).",
                document.Id);
            return;
        }

        // The ETO carries the DocumentTypeCode string (resolved from the internal DocumentTypeId, #207). A Ready
        // document always has a confirmed type because of the DeriveLifecycle gate (and the container case is handled
        // above), and DeleteAsync prevents deleting in-use types, so the type should be active.
        string? documentTypeCode = null;
        if (document.DocumentTypeId.HasValue)
        {
            var type = await _documentTypeRepository.FindAsync(document.DocumentTypeId.Value);
            documentTypeCode = type?.TypeCode;
        }

        await _distributedEventBus.PublishAsync(
            new DocumentReadyEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now,
                DocumentTypeCode = documentTypeCode,
                // #306: provenance link for a Scenario B sub-document (null for normally-uploaded documents).
                OriginDocumentId = document.OriginDocumentId
            });

        _logger.LogInformation(
            "Document {DocumentId} reached Ready lifecycle; DocumentReadyEto enqueued (type={DocTypeCode}).",
            document.Id, documentTypeCode);
    }
}

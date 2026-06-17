using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.DocumentAI.Documents.Pipelines.Lifecycle;

/// <summary>
/// Listens to <see cref="DocumentLifecycleStatusChangedEvent"/> and publishes
/// <see cref="DocumentReadyEto"/> when a document transitions to
/// <see cref="DocumentLifecycleStatus.Ready"/>. This is the trusted signal downstream consumers
/// subscribe to by default under CLAUDE.md "Outbound Event Contract".
/// <para>
/// The Ready gate is enforced by the classification stage: documents with insufficient automatic
/// classification confidence / no suitable type receive the blocking UnresolvedClassification reason
/// and enter the manual review queue, which keeps them out of Ready. Therefore this handler needs no
/// extra check: <c>NewStatus == Ready</c> implicitly means the gate passed. #346: a <b>container</b> is a
/// valid Ready outcome with <b>no</b> type — it reaches Ready with <c>DocumentTypeCode == null</c> and
/// <c>IsContainer == true</c> (it sets no blocking reason), so the published ETO carries that pairing.
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

        // ETO still carries the DocumentTypeCode string to preserve the outbound contract, resolving
        // it from internal DocumentTypeId (#207). A non-container Ready document has a confirmed type
        // because of the DeriveLifecycle gate, and DeleteAsync prevents deleting in-use types, so the
        // type should be active. #346: a container reaches Ready with DocumentTypeId null, so
        // documentTypeCode stays null — that null + IsContainer=true is the valid container outcome.
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
                // #346: container marker; downstream skips building a record from a container (DocumentTypeCode null).
                IsContainer = document.IsContainer,
                // #306: provenance link for a Scenario B sub-document (null for normally-uploaded documents).
                OriginDocumentId = document.OriginDocumentId
            });

        _logger.LogInformation(
            "Document {DocumentId} reached Ready lifecycle; DocumentReadyEto enqueued (type={DocTypeCode}).",
            document.Id, documentTypeCode);
    }
}

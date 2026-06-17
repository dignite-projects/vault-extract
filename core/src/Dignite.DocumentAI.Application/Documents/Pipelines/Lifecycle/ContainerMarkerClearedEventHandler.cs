using System;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents.Segments;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.DocumentAI.Documents.Pipelines.Lifecycle;

/// <summary>
/// Listens to <see cref="ContainerMarkerClearedEvent"/> (#349) and retracts the sub-documents a now-reclassified
/// container previously spawned. The container's <see cref="Document.IsContainer"/> marker just transitioned
/// true→false (operator reclassify via <c>ConfirmClassification</c>, or a high-confidence automatic
/// <c>ApplyAutomaticClassificationResult</c> re-recognition): it is now a single concrete-typed document, so the
/// derived sub-documents it had spawned are stale and would double-count downstream with no retraction signal.
/// <para>
/// Within the same transaction as the marker clear (local event), this handler:
/// <list type="bullet">
///   <item>loads the spawned sub-documents (<see cref="IDocumentRepository.GetListByOriginAsync"/>, those whose
///   <see cref="Document.OriginDocumentId"/> is the container) and soft-deletes each, publishing a
///   <see cref="DocumentDeletedEto"/> per sub-document — mirroring <c>DocumentAppService.DeleteAsync</c> so downstream
///   moves their derived data to a recoverable archived state;</item>
///   <item>removes the container's <see cref="DocumentSegment"/> work-queue rows (keyed by
///   <see cref="DocumentSegment.SourceDocumentId"/>), so a stale segmentation job finds nothing to resume and the
///   ledger no longer references a non-container.</item>
/// </list>
/// Multi-tenancy: this runs in the ambient context where the marker was cleared; the sub-documents and segment rows
/// share the container's <c>TenantId</c> and are matched by ABP's <c>IMultiTenant</c> global filter — the filter is
/// never pierced.
/// </para>
/// </summary>
public class ContainerMarkerClearedEventHandler
    : ILocalEventHandler<ContainerMarkerClearedEvent>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly ILogger<ContainerMarkerClearedEventHandler> _logger;

    public ContainerMarkerClearedEventHandler(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ILogger<ContainerMarkerClearedEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(ContainerMarkerClearedEvent eventData)
    {
        var containerId = eventData.DocumentId;

        // Retract the spawned sub-documents: soft-delete each + publish DocumentDeletedEto so downstream archives the
        // derived data. A container that was never segmented (or whose segmentation never spawned) yields an empty
        // list — a no-op, which is correct.
        var subDocuments = await _documentRepository.GetListByOriginAsync(containerId);
        foreach (var subDocument in subDocuments)
        {
            await _documentRepository.DeleteAsync(subDocument);

            await _distributedEventBus.PublishAsync(
                new DocumentDeletedEto
                {
                    DocumentId = subDocument.Id,
                    TenantId = subDocument.TenantId,
                    EventTime = _clock.Now
                });
        }

        // Remove the container's segment work-queue rows: they reference a document that is no longer a container, so a
        // stale segmentation job finds nothing to resume (DocumentSegment has no soft delete — it is working state).
        await _segmentRepository.DeleteAsync(s => s.SourceDocumentId == containerId);

        if (subDocuments.Count > 0)
        {
            _logger.LogInformation(
                "Container {ContainerId} reclassified to a concrete type; retracted {SubDocumentCount} spawned sub-document(s) and removed its segment rows (#349).",
                containerId, subDocuments.Count);
        }
    }
}

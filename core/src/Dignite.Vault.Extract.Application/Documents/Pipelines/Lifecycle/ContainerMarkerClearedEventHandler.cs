using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.Vault.Extract.Documents.Pipelines.Lifecycle;

/// <summary>
/// Listens to <see cref="ContainerMarkerClearedEvent"/> (#349) and retracts the sub-documents a now-reclassified
/// container previously spawned. The container's <see cref="Document.IsContainer"/> marker just transitioned
/// true→false (operator reclassify via <c>ConfirmClassification</c>, or a high-confidence automatic
/// <c>ApplyAutomaticClassificationResult</c> re-recognition): it is now a single concrete-typed document, so the
/// derived sub-documents it had spawned are stale and would double-count downstream with no retraction signal.
/// <para>
/// Within the same transaction as the marker clear (local event), this handler:
/// <list type="bullet">
///   <item>loads the sub-documents <b>this container's segmentation spawned</b> — identified by the container's
///   <see cref="DocumentSegment"/> ledger (<see cref="DocumentSegment.RoutedDocumentId"/>), <b>not</b> a blanket
///   <see cref="Document.OriginDocumentId"/> sweep — and soft-deletes each, publishing a
///   <see cref="DocumentDeletedEto"/> per sub-document so downstream moves their derived data to a recoverable
///   archived state. <b><see cref="DocumentSegmentKind.Figure"/> children are intentionally left intact</b>
///   (#364/#371; such rows are legacy-only since #487 retired figure routing): an embedded figure is orthogonal to
///   container-ness, so retracting its sub-document — possibly Ready and already consumed downstream — would lose a
///   legitimate routing;</item>
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

        // #364 / #371: retract ONLY the sub-documents THIS container's segmentation (#346) spawned, identified by the
        // container's DocumentSegment ledger (RoutedDocumentId is set on a Spawned segment). A blanket OriginDocumentId
        // sweep would also delete genuinely-embedded FIGURE-kind sub-documents — orthogonal to container-ness (a normal
        // concrete-typed document keeps its embedded sub-documents) — so drive the retraction from the segment rows and
        // filter by Kind (below). A container that was never segmented (or whose segmentation never spawned) yields no
        // routed ids — a no-op, which is correct.
        var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
        // #371: retract only container-bound (Text) sub-documents — bundle constituents that existed only because the
        // parent was a container. Container-independent (Figure) sub-documents — spawned by pre-#487 deployments
        // only, since #487 retired figure routing — are genuinely embedded documents (an invoice photo inside what
        // is now a concrete-typed contract) and survive the reclassify (#364). This in-memory filter goes
        // through the exhaustive IsContainerIndependent switch (#379 LOW), so a future third kind throws here (the
        // first decision site reached) rather than being silently kept; the SQL bulk delete below keeps the literal
        // Kind value it cannot translate a method call.
        var spawnedSubDocumentIds = segments
            .Where(s => !s.Kind.IsContainerIndependent() && s.RoutedDocumentId.HasValue)
            .Select(s => s.RoutedDocumentId!.Value)
            .ToList();

        var retractedCount = 0;
        if (spawnedSubDocumentIds.Count > 0)
        {
            // Bounded fan-out: segment count is capped upstream by VaultExtractBehaviorOptions.MaxSegmentsPerDocument
            // (default 50 — the segmentation job flags the container for review rather than spawn beyond that), so this
            // list is small and fixed: no Take(N)/pagination and no background job, the retraction runs synchronously
            // within the reclassify UoW (see the per-document delete note below).
            var subDocuments = await _documentRepository.GetListAsync(d => spawnedSubDocumentIds.Contains(d.Id));
            foreach (var subDocument in subDocuments)
            {
                // Intentional deferred flush: DeleteAsync WITHOUT autoSave (contrast the eager bulk DeleteAsync(predicate)
                // for segments below). The soft-delete UPDATE is deferred to the ambient reclassify UoW's SaveChanges, so
                // it commits together with that UoW. DO NOT add autoSave: true here — doing so would flush this one
                // sub-document mid-handler, partially committing before the rest of the retraction and before the
                // reclassify completes, breaking the all-in-one-UoW guarantee: every soft-delete, every DocumentDeletedEto,
                // and the DocumentClassifiedEto must land in a single transactional-outbox commit (atomic, all-or-nothing).
                await _documentRepository.DeleteAsync(subDocument);

                await _distributedEventBus.PublishAsync(
                    new DocumentDeletedEto
                    {
                        DocumentId = subDocument.Id,
                        TenantId = subDocument.TenantId,
                        EventTime = _clock.Now
                    });
                retractedCount++;
            }
        }

        // Remove the container's TEXT-kind segment work-queue rows: they reference a bundle premise that is gone, so a
        // stale detection job finds no text constituents to resume (DocumentSegment has no soft delete — working
        // state). FIGURE-kind rows (legacy-only since #487) are KEPT: a spawned figure's row shields its live
        // sub-document from the retraction above, and its SegmentKey remains the sole duplicate-spawn barrier (#481
        // moved spawn idempotency entirely onto this ledger); a still-Pending legacy figure row is deleted on
        // encounter by the segmentation job (#487), never routed.
        await _segmentRepository.DeleteAsync(s => s.SourceDocumentId == containerId && s.Kind == DocumentSegmentKind.Text);

        if (retractedCount > 0)
        {
            _logger.LogInformation(
                "Container {ContainerId} reclassified to a concrete type; retracted {SubDocumentCount} segmentation sub-document(s) and removed its segment rows (#349/#364); figure-routed sub-documents (#306) left intact.",
                containerId, retractedCount);
        }
    }
}

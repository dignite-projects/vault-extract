using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;

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
///   (#364/#371): an embedded figure is orthogonal to container-ness, so retracting its sub-document — possibly
///   Ready and already consumed downstream — would lose a legitimate routing;</item>
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
    private readonly SubDocumentRetractor _subDocumentRetractor;
    private readonly ILogger<ContainerMarkerClearedEventHandler> _logger;

    public ContainerMarkerClearedEventHandler(
        SubDocumentRetractor subDocumentRetractor,
        ILogger<ContainerMarkerClearedEventHandler> logger)
    {
        _subDocumentRetractor = subDocumentRetractor;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(ContainerMarkerClearedEvent eventData)
    {
        var containerId = eventData.DocumentId;

        // #364 / #371: retract ONLY the container-bound (Text) sub-documents THIS container's segmentation spawned,
        // identified by its ledger, and remove their rows. Figure sub-documents are genuinely embedded documents (an
        // invoice photo inside what is now a concrete-typed contract) and survive the reclassify, rows included. A
        // container that was never segmented (or whose segmentation never spawned) is a no-op. The retraction is
        // shared with re-parse (#660), which withdraws every kind instead.
        var retractedCount = await _subDocumentRetractor.RetractContainerBoundAsync(containerId);

        if (retractedCount > 0)
        {
            _logger.LogInformation(
                "Container {ContainerId} reclassified to a concrete type; retracted {SubDocumentCount} segmentation sub-document(s) and removed its segment rows (#349/#364); figure-routed sub-documents (#306) left intact.",
                containerId, retractedCount);
        }
    }
}

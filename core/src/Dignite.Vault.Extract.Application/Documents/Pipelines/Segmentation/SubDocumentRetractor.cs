using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.Segments;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.Vault.Extract.Documents.Pipelines.Segmentation;

/// <summary>
/// Withdraws sub-documents a source document's segmentation routed, and removes the matching
/// <see cref="DocumentSegment"/> ledger rows, so that "a live routed child ⟺ its ledger row exists" keeps holding
/// (<c>.claude/rules/sub-document-segmentation.md</c>). The two callers differ only in which kinds they withdraw:
/// <list type="bullet">
///   <item><see cref="RetractContainerBoundAsync"/> — a container→concrete reclassify (#349/#364). The Markdown is
///   unchanged, so only the bundle constituents (<see cref="DocumentSegmentKind.Text"/>) lose their premise; embedded
///   figures stand on their own and survive.</item>
///   <item><see cref="RetractAllAsync"/> — a re-parse (#660). The Markdown every child was split from is gone, so every
///   child goes, whatever its kind, and the new Markdown is split from scratch.</item>
/// </list>
/// Each withdrawn sub-document is soft-deleted with one <see cref="DocumentDeletedEto"/>. Both run inside the caller's
/// unit of work without <c>autoSave</c>, so the withdrawals, their events and the caller's own change land in one
/// transactional-outbox commit or not at all. The fan-out is bounded by
/// <c>VaultExtractBehaviorOptions.MaxSegmentsPerDocument</c>, which is why it runs synchronously, with no paging.
/// Sub-documents already in the recycle bin are not touched, but their ledger rows go too, so they can no longer be
/// restored (<c>DocumentAppService.RestoreAsync</c>).
/// </summary>
public class SubDocumentRetractor : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;

    public SubDocumentRetractor(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        IDistributedEventBus distributedEventBus,
        IClock clock)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
    }

    /// <summary>
    /// Container→concrete (#349/#364): withdraws the <see cref="DocumentSegmentKind.Text"/> sub-documents and removes
    /// their rows. <see cref="DocumentSegmentKind.Figure"/> rows are kept: a Spawned figure's row keeps its live
    /// sub-document routed and stays the duplicate-spawn barrier (#481), and a still-Pending figure row is a real
    /// constituent the re-enqueued pass must still spawn (#494).
    /// </summary>
    /// <returns>The number of sub-documents withdrawn.</returns>
    public virtual Task<int> RetractContainerBoundAsync(Guid sourceDocumentId)
    {
        // The in-memory filter goes through the exhaustive IsContainerIndependent switch (#379), so a future third
        // kind throws here instead of being silently kept; the row delete keeps the literal Kind value because the
        // query provider cannot translate the method call.
        return RetractAsync(
            sourceDocumentId,
            segment => !segment.Kind.IsContainerIndependent(),
            segment => segment.SourceDocumentId == sourceDocumentId && segment.Kind == DocumentSegmentKind.Text);
    }

    /// <summary>
    /// Re-parse (#660): withdraws every sub-document the source routed, of either kind, and removes every ledger row
    /// sourced from it.
    /// </summary>
    /// <returns>The number of sub-documents withdrawn.</returns>
    public virtual Task<int> RetractAllAsync(Guid sourceDocumentId)
    {
        return RetractAsync(
            sourceDocumentId,
            _ => true,
            segment => segment.SourceDocumentId == sourceDocumentId);
    }

    protected virtual async Task<int> RetractAsync(
        Guid sourceDocumentId,
        Func<DocumentSegment, bool> withdrawsSubDocument,
        Expression<Func<DocumentSegment, bool>> rowsToRemove)
    {
        // Driven by the ledger (RoutedDocumentId on a Spawned row), never by a blanket OriginDocumentId sweep: the
        // ledger is what says which children this source's segmentation routed.
        var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == sourceDocumentId);
        var subDocumentIds = segments
            .Where(s => s.RoutedDocumentId.HasValue && withdrawsSubDocument(s))
            .Select(s => s.RoutedDocumentId!.Value)
            .ToList();

        var withdrawn = 0;
        if (subDocumentIds.Count > 0)
        {
            var subDocuments = await _documentRepository.GetListAsync(d => subDocumentIds.Contains(d.Id));
            foreach (var subDocument in subDocuments)
            {
                // No autoSave: the soft-delete UPDATE is flushed with the caller's unit of work, so a partial
                // withdrawal can never commit ahead of the change that caused it.
                await _documentRepository.DeleteAsync(subDocument);

                await _distributedEventBus.PublishAsync(
                    new DocumentDeletedEto
                    {
                        DocumentId = subDocument.Id,
                        TenantId = subDocument.TenantId,
                        EventTime = _clock.Now
                    });
                withdrawn++;
            }
        }

        // DocumentSegment has no soft delete: it is working state, and the withdrawn sub-document is the restorable
        // artifact. With the row gone, a stale segmentation job finds nothing to resume.
        await _segmentRepository.DeleteAsync(rowsToRemove);

        return withdrawn;
    }
}

using System;
using System.Linq;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The single implementation of the document metadata predicate chain and of the default row order, shared by
/// the operator list (<c>DocumentAppService</c>) and the data download (<c>DocumentExportAppService</c>).
/// <para>
/// #501 item 1: the two call sites used to hand-write the same four predicates
/// (<c>LifecycleStatus</c> / <c>DocumentTypeId</c> / <c>CabinetId</c> / <c>OriginDocumentId</c>) independently, and
/// had already drifted. #500's client-side round-trip guard compares filter <b>key names</b> and is blind to
/// predicate <b>semantics</b>: widen <see cref="DocumentMetadataFilter.CabinetId"/> to include child cabinets on
/// the list, and a second literal <c>d.CabinetId == id</c> in the export would not follow — the file and the
/// screen diverge with nothing to catch it. That is the #496 bug class, one layer down. There is now one
/// implementation, so a change to any predicate reaches both egress paths or neither.
/// </para>
/// <para>
/// The <b>filter shape</b> is deliberately wider than either caller's DTO. Each caller populates only the knobs
/// its own contract exposes (the list has no date range; the export has no review disposition) and leaves the
/// rest null. Sharing the shape single-sources the <i>semantics</i> of every predicate without forcing the two
/// contracts to expose the same knobs.
/// </para>
/// <para>
/// Soft-delete is <b>not</b> part of this chain. The list opens <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> for the
/// recycle-bin view; the export never does, so deleted documents cannot reach a file. That asymmetry is
/// fail-closed and intentional — expressing it here as a nullable <c>IsDeleted</c> knob would invite a caller to
/// set it without opening the ambient filter, which silently returns nothing.
/// </para>
/// </summary>
public static class DocumentQueries
{
    /// <summary>
    /// Narrows <paramref name="query"/> by every non-null member of <paramref name="filter"/>, AND-combined.
    /// Null members are absent filters, never a filter for "none".
    /// </summary>
    public static IQueryable<Document> ApplyMetadataFilter(
        this IQueryable<Document> query, DocumentMetadataFilter filter)
    {
        if (filter.LifecycleStatus.HasValue)
            query = query.Where(d => d.LifecycleStatus == filter.LifecycleStatus.Value);

        // Type filtering uses the resolved internal DocumentTypeId (#207), never the external TypeCode.
        if (filter.DocumentTypeId.HasValue)
            query = query.Where(d => d.DocumentTypeId == filter.DocumentTypeId.Value);

        if (filter.CabinetId.HasValue)
            query = query.Where(d => d.CabinetId == filter.CabinetId.Value);

        // Sub-document provenance (#354): only the children derived from this source document. The IMultiTenant
        // global filter still applies, so both ends stay tenant-scoped.
        if (filter.OriginDocumentId.HasValue)
            query = query.Where(d => d.OriginDocumentId == filter.OriginDocumentId.Value);

        // Manual-review disposition phase (#284). Rejected documents carry ReviewDisposition=Rejected and can be
        // queried explicitly, which is why this is a separate axis from the queue predicate below.
        if (filter.ReviewDisposition.HasValue)
            query = query.Where(d => d.ReviewDisposition == filter.ReviewDisposition.Value);

        // Operator review queue (#284 / #333 / #395): the canonical DocumentReviewQueries.RequiresAttention
        // predicate, shared with the overview needs-review statistic. A second, hand-rolled "needs review"
        // predicate is precisely how a screen and a file quietly disagree.
        if (filter.HasReviewReasons == true)
            query = query.Where(DocumentReviewQueries.RequiresAttention);

        if (filter.CreationTimeMin.HasValue)
            query = query.Where(d => d.CreationTime >= filter.CreationTimeMin.Value.Date);
        // The upper bound includes the whole Max date (< Max + 1 day), matching date-picker intuition.
        if (filter.CreationTimeMax.HasValue)
            query = query.Where(d => d.CreationTime < filter.CreationTimeMax.Value.Date.AddDays(1));

        return query;
    }

    /// <summary>
    /// The default document order: by <c>CreationTime</c>, then by <c>Id</c> as a tiebreaker.
    /// <para>
    /// #501 item 5: <c>CreationTime</c> ties are ordinary for a batch upload, and without a tiebreaker the
    /// order is whatever the database happens to return — the same data lists (and exports) in a different order
    /// run to run, and a tied row sitting on a <c>Skip</c>/<c>Take</c> page boundary can appear on two pages or
    /// none. Both egress paths order through here so the file and the screen agree on ties too.
    /// </para>
    /// </summary>
    public static IQueryable<Document> OrderByCreationTime(this IQueryable<Document> query, bool descending)
    {
        return descending
            ? query.OrderByDescending(d => d.CreationTime).ThenByDescending(d => d.Id)
            : query.OrderBy(d => d.CreationTime).ThenBy(d => d.Id);
    }
}

/// <summary>
/// The metadata filters a document query can express, shared by the operator list and the data download so the
/// predicate behind each one has exactly one implementation (see <see cref="DocumentQueries"/>). A null member
/// is an absent filter.
/// </summary>
public class DocumentMetadataFilter
{
    /// <summary>Internal type id (#207), already resolved from the caller's external <c>DocumentTypeCode</c>.</summary>
    public Guid? DocumentTypeId { get; set; }

    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    /// <summary>Cabinet (#194): the manual filing dimension, orthogonal to the pipeline.</summary>
    public Guid? CabinetId { get; set; }

    /// <summary>Provenance (#354): the sub-documents derived from this source document.</summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>Review <b>disposition</b> axis (#284): confirmed / rejected / not reviewed.</summary>
    public DocumentReviewDisposition? ReviewDisposition { get; set; }

    /// <summary>Review <b>reason</b> axis (#284): true selects the operator review queue.</summary>
    public bool? HasReviewReasons { get; set; }

    public DateTime? CreationTimeMin { get; set; }

    public DateTime? CreationTimeMax { get; set; }
}

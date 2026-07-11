namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Operator overview statistics for the current layer (#333). An aggregated read-model only — no channel
/// boundary, no downstream business metrics. Scope is decided by ABP ambient filters: <c>IMultiTenant</c>
/// isolates the current layer (active tenant -> that tenant; no tenant -> Host) and <c>ISoftDelete</c>
/// excludes recycle-bin documents.
/// </summary>
public class DocumentStatisticsDto
{
    /// <summary>All non-deleted documents in the current layer. Equals the sum of the five lifecycle counts.</summary>
    public long TotalCount { get; set; }

    /// <summary>Stored but not yet started by any pipeline (<see cref="DocumentLifecycleStatus.Uploaded"/>).</summary>
    public long UploadedCount { get; set; }

    /// <summary>At least one critical pipeline is still running (<see cref="DocumentLifecycleStatus.Processing"/>).</summary>
    public long ProcessingCount { get; set; }

    /// <summary>
    /// Pipelines finished but a blocking review reason withholds Ready — waiting on the operator
    /// (<see cref="DocumentLifecycleStatus.PendingReview"/>, #510). Overlaps <see cref="NeedsReviewCount"/>
    /// (a PendingReview document always needs attention) but, unlike it, is part of the <see cref="TotalCount"/> partition.
    /// </summary>
    public long PendingReviewCount { get; set; }

    /// <summary>All critical pipelines succeeded (<see cref="DocumentLifecycleStatus.Ready"/>).</summary>
    public long ReadyCount { get; set; }

    /// <summary>A critical pipeline finally failed, or the document was operator-rejected (<see cref="DocumentLifecycleStatus.Failed"/>).</summary>
    public long FailedCount { get; set; }

    /// <summary>
    /// Documents needing operator attention: any unresolved review reason and not rejected
    /// (<c>ReviewReasons != None &amp;&amp; ReviewDisposition != Rejected</c>, the canonical predicate shared with the review queue).
    /// Orthogonal to lifecycle — it overlaps the status buckets and is NOT part of the <see cref="TotalCount"/> partition.
    /// </summary>
    public long NeedsReviewCount { get; set; }

    /// <summary>
    /// Total original uploaded size in bytes (sum of <c>FileOrigin.FileSize</c> across non-derived documents only —
    /// #481: a derived sub-document shares its parent's FileOrigin/size rather than owning distinct bytes, so it is
    /// excluded here to avoid multiplying the same storage by however many children exist). Not archive /
    /// native-payload size.
    /// </summary>
    public long TotalStorageBytes { get; set; }
}

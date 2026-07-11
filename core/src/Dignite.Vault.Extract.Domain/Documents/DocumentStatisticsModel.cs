namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Aggregate read-model for operator overview statistics (#333), returned by
/// <see cref="IDocumentRepository.GetStatisticsAsync"/>. A small Domain projection (not an entity):
/// per-lifecycle document counts, the needs-review count, and the total original upload size for the current
/// ambient layer. The Application layer maps this to <c>DocumentStatisticsDto</c>.
/// </summary>
public class DocumentStatisticsModel
{
    public long TotalCount { get; set; }

    public long UploadedCount { get; set; }

    public long ProcessingCount { get; set; }

    public long PendingReviewCount { get; set; }

    public long ReadyCount { get; set; }

    public long FailedCount { get; set; }

    public long NeedsReviewCount { get; set; }

    public long TotalStorageBytes { get; set; }
}

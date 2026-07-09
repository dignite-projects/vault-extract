using System;
using System.Reflection;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Integration tests for <see cref="IDocumentRepository.GetStatisticsAsync"/> (#333): per-lifecycle counts,
/// needs-review count, storage sum, recycle-bin exclusion, and current-layer (tenant) scoping.
/// </summary>
public class EfCoreDocumentRepositoryStatistics_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentTenant _currentTenant;

    public EfCoreDocumentRepositoryStatistics_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task GetStatistics_Returns_Zeroes_When_No_Documents()
    {
        var stats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());

        stats.TotalCount.ShouldBe(0);
        stats.UploadedCount.ShouldBe(0);
        stats.ProcessingCount.ShouldBe(0);
        stats.ReadyCount.ShouldBe(0);
        stats.FailedCount.ShouldBe(0);
        stats.NeedsReviewCount.ShouldBe(0);
        stats.TotalStorageBytes.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatistics_Counts_By_Lifecycle_NeedsReview_And_Sums_Storage()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedDocAsync(DocumentLifecycleStatus.Uploaded, 100);
            await SeedDocAsync(DocumentLifecycleStatus.Processing, 200);
            await SeedDocAsync(DocumentLifecycleStatus.Ready, 300);
            await SeedDocAsync(DocumentLifecycleStatus.Ready, 400);
            await SeedDocAsync(DocumentLifecycleStatus.Failed, 500);
            // Needs review: an unresolved reason is set and it is not rejected; lifecycle stays Uploaded.
            await SeedDocAsync(DocumentLifecycleStatus.Uploaded, 50, needsReview: true);
            // Rejected: carries a reason but disposition == Rejected, so it does NOT count as needs-review;
            // RejectReview also transitions it to Failed.
            await SeedDocAsync(DocumentLifecycleStatus.Failed, 60, rejected: true);

            // Soft-deleted Ready doc: excluded from every count and the storage sum.
            var deleted = NewDocument(9999);
            deleted.TransitionLifecycle(DocumentLifecycleStatus.Ready);
            await _documentRepository.InsertAsync(deleted, autoSave: true);
            await _documentRepository.DeleteAsync(deleted.Id);
        });

        var stats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());

        stats.TotalCount.ShouldBe(7);            // 8 active inserts - 1 soft-deleted
        stats.UploadedCount.ShouldBe(2);         // plain Uploaded + needs-review (still Uploaded)
        stats.ProcessingCount.ShouldBe(1);
        stats.ReadyCount.ShouldBe(2);
        stats.FailedCount.ShouldBe(2);           // explicit Failed + rejected (-> Failed)
        stats.NeedsReviewCount.ShouldBe(1);      // the rejected one is excluded
        stats.TotalStorageBytes.ShouldBe(1610);  // 100+200+300+400+500+50+60
    }

    [Fact]
    public async Task GetStatistics_Is_Scoped_To_Current_Layer()
    {
        var tenantId = Guid.NewGuid();

        // Host layer: one Ready document.
        await WithUnitOfWorkAsync(() => SeedDocAsync(DocumentLifecycleStatus.Ready, 100));

        // Tenant layer: two Ready documents.
        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(tenantId))
            {
                await SeedDocAsync(DocumentLifecycleStatus.Ready, 200);
                await SeedDocAsync(DocumentLifecycleStatus.Ready, 300);
            }
        });

        // Host sees only its own document; no cross-layer union.
        var hostStats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());
        hostStats.TotalCount.ShouldBe(1);
        hostStats.TotalStorageBytes.ShouldBe(100);

        // Tenant sees only its two documents.
        var tenantStats = await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(tenantId))
            {
                return await _documentRepository.GetStatisticsAsync();
            }
        });
        tenantStats.TotalCount.ShouldBe(2);
        tenantStats.TotalStorageBytes.ShouldBe(500);
    }

    [Fact]
    public async Task GetStatistics_Excludes_Containers()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // A normal Ready business document is counted.
            await SeedDocAsync(DocumentLifecycleStatus.Ready, 100);

            // #346: a container is an infrastructure wrapper, not a business document — excluded from the overview
            // (its sub-documents are the real records), even though it reached Ready.
            var container = NewDocument(999);
            typeof(Document)
                .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(container, null);
            container.TransitionLifecycle(DocumentLifecycleStatus.Ready);
            await _documentRepository.InsertAsync(container, autoSave: true);
        });

        var stats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());

        stats.TotalCount.ShouldBe(1);          // the container is excluded
        stats.ReadyCount.ShouldBe(1);          // only the normal Ready document
        stats.TotalStorageBytes.ShouldBe(100); // the container's bytes are excluded
    }

    [Fact]
    public async Task GetStatistics_Counts_SegmentationIncomplete_Container_In_NeedsReview_But_Not_Totals()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // A normal Ready business document.
            await SeedDocAsync(DocumentLifecycleStatus.Ready, 100);

            // #346: a container whose segmentation failed is excluded from totals/storage (it's a wrapper) but MUST
            // be counted in NeedsReview, because it also appears in the operator review queue — so the dashboard
            // count and the queue do not drift (#333).
            var container = NewDocument(999);
            typeof(Document)
                .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(container, null);
            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            container.TransitionLifecycle(DocumentLifecycleStatus.Ready);
            await _documentRepository.InsertAsync(container, autoSave: true);
        });

        var stats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());

        stats.TotalCount.ShouldBe(1);          // container excluded from the document total
        stats.ReadyCount.ShouldBe(1);          // only the normal Ready document
        stats.NeedsReviewCount.ShouldBe(1);    // but the segmentation-incomplete container IS counted as needing review
        stats.TotalStorageBytes.ShouldBe(100); // container bytes excluded
    }

    [Fact]
    public async Task GetStatistics_Excludes_Derived_Documents_From_The_Storage_Sum_But_Still_Counts_Them()
    {
        // A derived sub-document is excluded from the storage sum by OriginDocumentId != null, defensively, even
        // in the unusual case where it carries a non-null FileOrigin (normally it carries none at all). Document
        // counts are unaffected -- a derived document is still a real document and counts normally in its lifecycle
        // bucket.
        await WithUnitOfWorkAsync(async () =>
        {
            var parent = NewDocument(1000);
            parent.TransitionLifecycle(DocumentLifecycleStatus.Ready);
            await _documentRepository.InsertAsync(parent, autoSave: true);

            // CloneFileOrigin builds an equal-valued (not same-reference) copy of the parent's FileOrigin -- see its
            // XML doc for why a same-reference FileOrigin instance cannot be attached to two Document rows within
            // one DbContext. parent.FileOrigin is never null here (constructed by NewDocument just above).
            var child = Document.CreateDerived(
                _guidGenerator.Create(),
                _currentTenant.Id,
                CloneFileOrigin(parent.FileOrigin!),
                originDocumentId: parent.Id,
                originConstituentKey: "slice-1");
            child.TransitionLifecycle(DocumentLifecycleStatus.Ready);
            await _documentRepository.InsertAsync(child, autoSave: true);
        });

        var stats = await WithUnitOfWorkAsync(() => _documentRepository.GetStatisticsAsync());

        stats.TotalCount.ShouldBe(2);           // both the parent and the derived child count as real documents
        stats.ReadyCount.ShouldBe(2);
        stats.TotalStorageBytes.ShouldBe(1000);  // the child's shared FileSize is NOT summed a second time
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private async Task SeedDocAsync(
        DocumentLifecycleStatus status,
        long fileSize,
        bool needsReview = false,
        bool rejected = false)
    {
        var doc = NewDocument(fileSize);

        if (needsReview || rejected)
        {
            doc.SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: true);
        }

        if (rejected)
        {
            doc.RejectReview("rejected for test"); // -> ReviewDisposition.Rejected + LifecycleStatus.Failed
        }
        else if (status != DocumentLifecycleStatus.Uploaded)
        {
            doc.TransitionLifecycle(status);
        }

        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private Document NewDocument(long fileSize) =>
        new(
            _guidGenerator.Create(),
            _currentTenant.Id,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: fileSize,
                originalFileName: "test.pdf"));
}

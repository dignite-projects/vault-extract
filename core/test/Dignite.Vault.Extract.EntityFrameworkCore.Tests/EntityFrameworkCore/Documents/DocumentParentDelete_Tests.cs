using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentParentDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The delete paths publish lifecycle ETOs and the wider app-service graph touches these out-of-process
        // collaborators; substitute them so the test exercises the real DB + the real AnyByOriginAsync guard query.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the #508 <c>DocumentAppService</c> sub-document delete guards, against the real SQLite DB
/// so the <c>AnyByOriginAsync</c> EXISTS query and its interaction with the ambient <c>ISoftDelete</c> filter are
/// actually exercised (the AppService unit tests only mock the repository).
/// <para>
/// The two guards differ in exactly one respect, and that asymmetry is what most of these tests pin down:
/// </para>
/// <list type="bullet">
/// <item><description><c>DeleteAsync</c> (soft) blocks on <b>live</b> children only. It is reversible and leaves
/// the blob intact, so children already in the recycle bin are irrelevant to it.</description></item>
/// <item><description><c>PermanentDeleteAsync</c> blocks on <b>any</b> child, recycle-bin ones included: it
/// destroys the blob they reach through <c>OriginDocumentId</c>, and a recycle-bin child is restorable.</description></item>
/// </list>
/// Neither guard cascades: a blocked delete leaves the source and every child exactly as they were.
/// </summary>
public class DocumentParentDelete_Tests
    : VaultExtractTestBase<DocumentParentDeleteTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;

    public DocumentParentDelete_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    // === DeleteAsync (soft) ===

    [Fact]
    public async Task DeleteAsync_Is_Blocked_While_The_Source_Has_Live_SubDocuments()
    {
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.DeleteAsync(sourceId));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.HasSubDocuments);

        await WithUnitOfWorkAsync(async () =>
        {
            // Nothing moved: the source is still live and every child is untouched.
            (await _documentRepository.FindAsync(sourceId)).ShouldNotBeNull();
            foreach (var childId in childIds)
            {
                (await _documentRepository.GetAsync(childId)).IsDeleted.ShouldBeFalse();
            }
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentDeletedEto>());
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_When_The_Source_Has_No_SubDocuments()
    {
        var (sourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 0);

        await _appService.DeleteAsync(sourceId);

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(sourceId)).ShouldBeNull());

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == sourceId));
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_When_The_SubDocuments_Are_All_Already_In_The_Recycle_Bin()
    {
        // The ambient ISoftDelete filter excludes recycle-bin children from the guard's EXISTS, so a source whose
        // sub-documents are all already deleted stays soft-deletable. This is the DeleteAsync half of the asymmetry:
        // the same arrangement blocks PermanentDeleteAsync (see the twin test below).
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);
        await SoftDeleteChildrenAsync(childIds);

        await _appService.DeleteAsync(sourceId);

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(sourceId)).ShouldBeNull());

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == sourceId));
    }

    [Fact]
    public async Task DeleteAsync_Ignores_SubDocuments_Of_A_Different_Source()
    {
        // The guard filters on OriginDocumentId, so another source's children never block this one.
        var (otherSourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 2);
        var (sourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 0);

        await _appService.DeleteAsync(sourceId);

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.FindAsync(sourceId)).ShouldBeNull();
            (await _documentRepository.FindAsync(otherSourceId)).ShouldNotBeNull();
        });
    }

    // === PermanentDeleteAsync (hard delete + blob reclaim) ===

    [Fact]
    public async Task PermanentDeleteAsync_Is_Blocked_While_The_Source_Has_Live_SubDocuments()
    {
        var (sourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.PermanentDeleteAsync(sourceId));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.HasSubDocumentsPermanentDelete);

        // The guard runs before any mutation: the row survives and its blob was never reclaimed.
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(sourceId)).ShouldNotBeNull());

        await _blobContainer.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentPermanentlyDeletedEto>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Is_Blocked_When_The_SubDocuments_Are_Only_In_The_Recycle_Bin()
    {
        // The other half of the asymmetry, and the reason PermanentDeleteAsync runs its guard inside the
        // ISoftDelete-disabled scope: hard-deleting the source reclaims the blob its children reach through
        // OriginDocumentId, and a recycle-bin child is restorable — restoring it afterwards would yield a document
        // that can never reach its source.
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);
        await SoftDeleteChildrenAsync(childIds);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.PermanentDeleteAsync(sourceId));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.HasSubDocumentsPermanentDelete);

        await _blobContainer.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Succeeds_Once_Every_SubDocument_Is_Permanently_Deleted()
    {
        // The escape hatch the guard leaves open, so the operator is never deadlocked: a sub-document has no
        // children of its own, so its own permanent delete is unguarded. Purge the children, then the source goes.
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        foreach (var childId in childIds)
        {
            await _appService.PermanentDeleteAsync(childId);
        }

        await _appService.PermanentDeleteAsync(sourceId);

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                (await _documentRepository.FindAsync(sourceId)).ShouldBeNull();
                foreach (var childId in childIds)
                {
                    (await _documentRepository.FindAsync(childId)).ShouldBeNull();
                }
            }
        });

        // Only the source owns a blob (a sub-document carries no FileOrigin since #487), so exactly one reclaim.
        await _blobContainer.Received(1).DeleteAsync(SourceBlobName(sourceId), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentPermanentlyDeletedEto>(e => e.DocumentId == sourceId));
    }

    [Fact]
    public async Task PermanentDeleteAsync_Succeeds_When_The_Source_Has_No_SubDocuments()
    {
        var (sourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 0);

        await _appService.PermanentDeleteAsync(sourceId);

        await _blobContainer.Received(1).DeleteAsync(SourceBlobName(sourceId), Arg.Any<CancellationToken>());
    }

    // === Arrangement ===

    private static string SourceBlobName(Guid sourceId) => $"blobs/{sourceId:N}.pdf";

    /// <summary>
    /// Seeds one source document plus <paramref name="childCount"/> derived sub-documents
    /// (<see cref="Document.CreateDerived"/>, <c>OriginDocumentId</c> == source). Each child is spawned with
    /// <c>fileOrigin: null</c>, matching what <c>DerivedDocumentSpawner</c> actually produces since #487 — that
    /// nullness is the whole reason the guard exists, so the fixture must not paper over it with a stand-in.
    /// Returns the source id and the child ids.
    /// </summary>
    private async Task<(Guid SourceId, Guid[] ChildIds)> ArrangeSourceWithChildrenAsync(int childCount)
    {
        var sourceId = _guidGenerator.Create();
        var childIds = new Guid[childCount];

        await WithUnitOfWorkAsync(async () =>
        {
            var source = new Document(
                sourceId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: SourceBlobName(sourceId),
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));
            await _documentRepository.InsertAsync(source, autoSave: true);

            for (var i = 0; i < childCount; i++)
            {
                var childId = _guidGenerator.Create();
                childIds[i] = childId;
                var derived = Document.CreateDerived(
                    childId,
                    tenantId: null,
                    fileOrigin: null,
                    originDocumentId: sourceId,
                    originConstituentKey: $"constituent-{i}");
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
        });

        return (sourceId, childIds);
    }

    /// <summary>Sends the given children to the recycle bin (soft delete), bypassing the app service.</summary>
    private async Task SoftDeleteChildrenAsync(Guid[] childIds)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            foreach (var childId in childIds)
            {
                await _documentRepository.DeleteAsync(childId, autoSave: true);
            }
        });
    }
}

using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
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
        // DeleteAsync publishes a DocumentDeletedEto and the wider app-service graph touches these out-of-process
        // collaborators; substitute them so the test exercises the real DB.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for <c>DocumentAppService.DeleteAsync</c> against a source document that still has derived
/// sub-documents (#481, formerly <c>DocumentDeleteGuard_Tests</c>): the guard that used to block this soft-delete
/// is removed entirely — children now own a real <see cref="Document.FileOrigin"/> and a fully independent
/// lifecycle, so deleting a parent never cascades to them, and a dangling <see cref="Document.OriginDocumentId"/>
/// provenance pointer left on a soft-deleted source is accepted (downstream consumes provenance at Ready time).
/// Runs against the real SQLite DB so the source's soft-delete and the children's untouched liveness are actually
/// exercised (the AppService unit tests only mock the repository).
/// </summary>
public class DocumentParentDelete_Tests
    : VaultExtractTestBase<DocumentParentDeleteTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentParentDelete_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_While_The_Source_Still_Has_Live_SubDocuments()
    {
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        // No guard trips — the delete just succeeds.
        await _appService.DeleteAsync(sourceId);

        await WithUnitOfWorkAsync(async () =>
        {
            // The source itself is soft-deleted (invisible under the ambient ISoftDelete filter).
            (await _documentRepository.FindAsync(sourceId)).ShouldBeNull();

            // Deleting the parent never cascades (#481): each child remains fully live, and its OriginDocumentId
            // provenance pointer still resolves to the (now soft-deleted) source id — a dangling-but-accepted
            // pointer, not a broken one.
            foreach (var childId in childIds)
            {
                var child = await _documentRepository.GetAsync(childId);
                child.IsDeleted.ShouldBeFalse();
                child.OriginDocumentId.ShouldBe(sourceId);
            }
        });

        // Exactly one DocumentDeletedEto fired, for the source only — no cascade publish to children either.
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == sourceId));
    }

    /// <summary>
    /// Seeds one source document plus <paramref name="childCount"/> derived sub-documents
    /// (<see cref="Document.CreateDerived"/>, <c>OriginDocumentId</c> == source). FileOrigin is required on every
    /// document (#481); this guard test only cares about the OriginDocumentId back-reference, so the child's
    /// FileOrigin is a fake stand-in, not the real #481 shared-parent-blob value <c>DerivedDocumentSpawner</c>
    /// produces. Returns the source id and the child ids.
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
                    blobName: $"blobs/{sourceId:N}.pdf",
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
                    fileOrigin: new FileOrigin(
                        blobName: $"blobs/{sourceId:N}.pdf",
                        uploadedByUserName: "test-user",
                        contentType: "application/pdf",
                        contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                        fileSize: 2048,
                        originalFileName: "bundle.pdf"),
                    originDocumentId: sourceId,
                    originConstituentKey: $"constituent-{i}");
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
        });

        return (sourceId, childIds);
    }
}

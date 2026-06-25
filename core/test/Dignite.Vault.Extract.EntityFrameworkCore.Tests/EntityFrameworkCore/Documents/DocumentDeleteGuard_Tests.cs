using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(ExtractEntityFrameworkCoreTestModule))]
public class DocumentDeleteGuardTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // DeleteAsync publishes a DocumentDeletedEto and the wider app-service graph touches these out-of-process
        // collaborators; substitute them so the test exercises the real DB + the real AnyByOriginAsync guard query.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<ExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the <c>DocumentAppService.DeleteAsync</c> sub-document guard: a source document must not be
/// sent to the recycle bin while it still has <b>live</b> derived sub-documents (<see cref="Document.OriginDocumentId"/>),
/// otherwise their provenance back-reference would dangle and their detail page could no longer resolve the now-gone
/// source. Runs against the real SQLite DB so the <c>AnyByOriginAsync</c> EXISTS query and the ambient
/// <c>ISoftDelete</c> filter interaction are actually exercised (the AppService unit tests only mock the repository).
/// </summary>
public class DocumentDeleteGuard_Tests
    : ExtractTestBase<DocumentDeleteGuardTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentDeleteGuard_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task DeleteAsync_Is_Blocked_While_The_Source_Has_Live_SubDocuments()
    {
        var (sourceId, _) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.DeleteAsync(sourceId));
        exception.Code.ShouldBe(ExtractErrorCodes.Document.HasSubDocuments);

        // Fail closed: the source is still live (not sent to the recycle bin).
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(sourceId)).IsDeleted.ShouldBeFalse());

        // No DocumentDeletedEto fired for the blocked delete.
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentDeletedEto>());
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_Once_The_SubDocuments_Are_Removed()
    {
        var (sourceId, childIds) = await ArrangeSourceWithChildrenAsync(childCount: 2);

        // Remove (soft-delete) the children first — the operator-driven path the guard nudges toward. Once they are in
        // the recycle bin the ambient ISoftDelete filter excludes them, so AnyByOriginAsync no longer sees live children.
        await WithUnitOfWorkAsync(async () =>
        {
            foreach (var childId in childIds)
            {
                await _documentRepository.DeleteAsync(childId, autoSave: true);
            }
        });

        await _appService.DeleteAsync(sourceId);

        // The source is now soft-deleted (invisible under the default filter) and exactly one DocumentDeletedEto fired.
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(sourceId)).ShouldBeNull());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == sourceId));
    }

    /// <summary>
    /// Seeds one source document plus <paramref name="childCount"/> derived sub-documents
    /// (<see cref="Document.CreateDerived"/>, <c>OriginDocumentId</c> == source). Sub-documents carry no source blob,
    /// mirroring the segmentation spawn. Returns the source id and the child ids.
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
                    fileOrigin: null,
                    originDocumentId: sourceId,
                    originConstituentKey: $"constituent-{i}");
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
        });

        return (sourceId, childIds);
    }
}

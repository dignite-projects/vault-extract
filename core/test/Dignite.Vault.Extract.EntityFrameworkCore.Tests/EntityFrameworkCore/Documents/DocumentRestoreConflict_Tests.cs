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
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentRestoreConflictTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // RestoreAsync itself touches neither blob storage nor background jobs, but the wider DocumentAppService
        // constructor graph does; substitute them so the test exercises the real DB (mirrors DocumentParentDelete_Tests).
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the #485 restore-time fail-close: <c>DocumentAppService.RestoreAsync</c> rejects
/// restoring a derived document whose <c>(OriginDocumentId, OriginConstituentKey)</c> identity is already occupied
/// by another LIVE document — the application-layer replacement for the fail-close the #481-dropped #391
/// filtered-unique index used to give for free. Runs against the real SQLite DB (not a mocked repository) so
/// <see cref="IDocumentRepository.AnyLiveDerivedDuplicateAsync"/>'s explicit soft-delete exclusion, run inside the
/// caller's <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> scope, is genuinely exercised.
/// </summary>
public class DocumentRestoreConflict_Tests : VaultExtractTestBase<DocumentRestoreConflictTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;

    public DocumentRestoreConflict_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    [Fact]
    public async Task RestoreAsync_Rejects_A_SoftDeleted_Child_When_A_Live_Successor_Shares_Its_Identity()
    {
        // C_old: a retracted child (e.g. a container->type retraction, #349/#364) later soft-deleted.
        // C_new: a live re-spawned successor sharing the SAME (OriginDocumentId, OriginConstituentKey) -- #481
        // dropped the DB-level filtered-unique index that used to make this pair impossible, so both rows coexist.
        var sourceId = _guidGenerator.Create();
        var oldChildId = _guidGenerator.Create();
        var newChildId = _guidGenerator.Create();
        const string constituentKey = "slice-1";

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(
                NewDerivedDocument(oldChildId, sourceId, constituentKey), autoSave: true);
            await _documentRepository.InsertAsync(
                NewDerivedDocument(newChildId, sourceId, constituentKey), autoSave: true);
        });

        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(oldChildId));

        var exception = await Should.ThrowAsync<BusinessException>(
            () => _appService.RestoreAsync(oldChildId));
        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreConflict);

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                (await _documentRepository.GetAsync(oldChildId)).IsDeleted.ShouldBeTrue();
            }
        });
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentRestoredEto>());
    }

    [Fact]
    public async Task RestoreAsync_Succeeds_And_Publishes_DocumentRestoredEto_When_No_Live_Duplicate_Exists()
    {
        var sourceId = _guidGenerator.Create();
        var childId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(() => _documentRepository.InsertAsync(
            NewDerivedDocument(childId, sourceId, "slice-1"), autoSave: true));
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(childId));

        await _appService.RestoreAsync(childId);

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(childId)).IsDeleted.ShouldBeFalse());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentRestoredEto>(e => e.DocumentId == childId));
    }

    private Document NewDerivedDocument(Guid id, Guid sourceId, string constituentKey) =>
        Document.CreateDerived(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{sourceId:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                fileSize: 2048,
                originalFileName: "bundle.pdf"),
            originDocumentId: sourceId,
            originConstituentKey: constituentKey);
}

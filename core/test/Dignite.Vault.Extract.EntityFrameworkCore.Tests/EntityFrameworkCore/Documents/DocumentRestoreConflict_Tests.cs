using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
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
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;

    public DocumentRestoreConflict_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
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
        // #660: the child is still routed by its source's ledger, the state an operator's own soft delete leaves.
        var sourceId = await InsertSourceAsync();
        var childId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(NewDerivedDocument(childId, sourceId, "slice-1"), autoSave: true);
            await InsertLedgerRowAsync(sourceId, "slice-1", childId);
        });
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(childId));

        await _appService.RestoreAsync(childId);

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(childId)).IsDeleted.ShouldBeFalse());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentRestoredEto>(e => e.DocumentId == childId));
    }

    [Fact]
    public async Task RestoreAsync_Rejects_A_Child_Its_Source_Ledger_No_Longer_Routes()
    {
        // #660: "a live routed child <=> its ledger row exists". The row is gone (a re-parse re-split the source, or a
        // container->concrete reclassify retracted it), so the child was superseded, and restoring it would put it next
        // to whatever replaced it. No live duplicate shares its key, so only the ledger check stands in the way.
        var sourceId = await InsertSourceAsync();
        var childId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(() => _documentRepository.InsertAsync(
            NewDerivedDocument(childId, sourceId, "slice-1"), autoSave: true));
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(childId));

        var exception = await Should.ThrowAsync<BusinessException>(() => _appService.RestoreAsync(childId));
        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreSuperseded);

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                (await _documentRepository.GetAsync(childId)).IsDeleted.ShouldBeTrue();
            }
        });
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentRestoredEto>());
    }

    [Fact]
    public async Task RestoreAsync_Rejects_A_Child_Whose_Ledger_Row_Routes_Another_Document()
    {
        // The key is still in the ledger, but routed to a different child (itself in the recycle bin, so the
        // live-duplicate check does not fire). This child is not the one the source's split produced.
        var sourceId = await InsertSourceAsync();
        var childId = _guidGenerator.Create();
        var otherChildId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(NewDerivedDocument(childId, sourceId, "slice-1"), autoSave: true);
            await _documentRepository.InsertAsync(NewDerivedDocument(otherChildId, sourceId, "slice-1"), autoSave: true);
            await InsertLedgerRowAsync(sourceId, "slice-1", otherChildId);
        });
        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.DeleteAsync(childId);
            await _documentRepository.DeleteAsync(otherChildId);
        });

        var exception = await Should.ThrowAsync<BusinessException>(() => _appService.RestoreAsync(childId));
        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreSuperseded);
    }

    private async Task<Guid> InsertSourceAsync()
    {
        // The ledger row's FK needs a real source row.
        var sourceId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => _documentRepository.InsertAsync(
            new Document(sourceId, tenantId: null, fileOrigin: new FileOrigin(
                blobName: $"blobs/{sourceId:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                fileSize: 2048,
                originalFileName: "bundle.pdf")),
            autoSave: true));
        return sourceId;
    }

    private async Task InsertLedgerRowAsync(Guid sourceId, string segmentKey, Guid routedDocumentId)
    {
        var segment = new DocumentSegment(
            _guidGenerator.Create(), tenantId: null, sourceDocumentId: sourceId,
            segmentKey: segmentKey, sliceText: segmentKey, ordinal: 0, kind: DocumentSegmentKind.Text);
        segment.MarkSpawned(routedDocumentId);
        await _segmentRepository.InsertAsync(segment, autoSave: true);
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
            originConstituentKey: constituentKey, creatorId: null);
}

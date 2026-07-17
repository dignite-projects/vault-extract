using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class CabinetDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The restore path publishes a lifecycle ETO and the wider DocumentAppService graph touches these
        // out-of-process collaborators; substitute them so the test exercises the real DB + the real cleanup query.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the #530 cabinet-deletion cleanup, run against the real SQLite DB so the unfile query
/// actually meets ABP's ambient <c>ISoftDelete</c> / <c>IMultiTenant</c> global filters. The pre-existing
/// <c>CabinetAppService_Tests</c> mock the repository, which cannot observe a global filter at all — they would stay
/// green with this fix removed, so the behaviour is pinned here instead.
/// <para>
/// The invariant: cabinet membership is optional organization metadata, not document identity or pipeline state, so
/// deleting a cabinet is always allowed and means "unfile every referencing document in this layer" — including the
/// ones already in the recycle bin. Contrast <c>DocumentTypeDelete_Tests</c>, where schema identity makes the same
/// situation fail closed instead.
/// </para>
/// </summary>
public class CabinetDelete_Tests : VaultExtractTestBase<CabinetDeleteTestModule>
{
    private readonly ICabinetAppService _cabinetAppService;
    private readonly IDocumentAppService _documentAppService;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;

    private static readonly Guid OtherTenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public CabinetDelete_Tests()
    {
        _cabinetAppService = GetRequiredService<ICabinetAppService>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task DeleteAsync_Unfiles_Every_Live_Document_In_The_Cabinet()
    {
        var cabinetId = await ArrangeCabinetAsync();
        var firstId = await ArrangeDocumentAsync(cabinetId);
        var secondId = await ArrangeDocumentAsync(cabinetId);

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(cabinetId));

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(firstId)).CabinetId.ShouldBeNull();
            (await _documentRepository.GetAsync(secondId)).CabinetId.ShouldBeNull();
            (await _cabinetRepository.FindAsync(cabinetId)).ShouldBeNull();
        });
    }

    [Fact]
    public async Task DeleteAsync_Unfiles_Recycle_Bin_Documents_Too()
    {
        // #530 itself. Before the fix the cleanup ran under the ambient ISoftDelete filter, so only live documents
        // were unfiled and "delete cabinet" meant two different things depending on lifecycle state: a binned
        // document kept hidden membership in a soft-deleted cabinet, which no active cabinet read can resolve — the
        // UI shows it as unfiled while the persisted CabinetId still points at the deleted row.
        var cabinetId = await ArrangeCabinetAsync();
        var documentId = await ArrangeDocumentAsync(cabinetId);
        await SoftDeleteDocumentAsync(documentId);

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(cabinetId));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var document = await _documentRepository.GetAsync(documentId);
                document.CabinetId.ShouldBeNull();
                // Unfiling a recycle-bin document must not resurrect it: only CabinetId moves.
                document.IsDeleted.ShouldBeTrue();
            }
        });
    }

    [Fact]
    public async Task DeleteAsync_Leaves_A_Restored_Document_Unfiled()
    {
        // The user-visible symptom #530 reports: the document comes back from the recycle bin filed into a cabinet
        // that no longer exists. After the fix it comes back genuinely unfiled.
        var cabinetId = await ArrangeCabinetAsync();
        var documentId = await ArrangeDocumentAsync(cabinetId);
        await SoftDeleteDocumentAsync(documentId);

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(cabinetId));
        await WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(documentId)).CabinetId.ShouldBeNull());
    }

    [Fact]
    public async Task DeleteAsync_Does_Not_Touch_Documents_In_Another_Cabinet()
    {
        var deletedCabinetId = await ArrangeCabinetAsync("Legal");
        var survivingCabinetId = await ArrangeCabinetAsync("Finance");
        var survivorId = await ArrangeDocumentAsync(survivingCabinetId);

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(deletedCabinetId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(survivorId)).CabinetId.ShouldBe(survivingCabinetId));
    }

    [Fact]
    public async Task DeleteAsync_Does_Not_Touch_Another_Layers_Documents()
    {
        // The risk the #530 change itself introduces: disabling ISoftDelete for the cleanup must NOT take
        // IMultiTenant down with it. Another layer's document must never be read or written by this cleanup.
        var cabinetId = await ArrangeCabinetAsync();
        var otherLayerDocumentId = await ArrangeDocumentAsync(cabinetId, tenantId: OtherTenantId);

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(cabinetId));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(OtherTenantId))
            {
                (await _documentRepository.GetAsync(otherLayerDocumentId)).CabinetId.ShouldBe(cabinetId);
            }
        });
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_For_An_Empty_Cabinet()
    {
        var cabinetId = await ArrangeCabinetAsync();

        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(cabinetId));

        await WithUnitOfWorkAsync(async () =>
            (await _cabinetRepository.FindAsync(cabinetId)).ShouldBeNull());
    }

    // === Arrangement ===

    private async Task<Guid> ArrangeCabinetAsync(string name = "Legal")
    {
        var cabinetId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() =>
            _cabinetRepository.InsertAsync(new Cabinet(cabinetId, tenantId: null, name), autoSave: true));
        return cabinetId;
    }

    private async Task<Guid> ArrangeDocumentAsync(Guid cabinetId, Guid? tenantId = null)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(tenantId))
            {
                await _documentRepository.InsertAsync(
                    new Document(documentId, tenantId, DocumentTestData.NewFileOrigin(documentId), cabinetId),
                    autoSave: true);
            }
        });
        return documentId;
    }

    private Task SoftDeleteDocumentAsync(Guid documentId) =>
        WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId, autoSave: true));
}

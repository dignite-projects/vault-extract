using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Cabinets;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class CabinetAppServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
    }
}

/// <summary>
/// Behavior tests for <see cref="CabinetAppService.DeleteAsync"/>. The core is cabinet deletion ownership
/// handling identified by Codex adversarial review (#194): deleting a cabinet must atomically clear
/// CabinetId on documents in that cabinet, truly unfiling them; otherwise documents point to a deleted
/// cabinet.
/// </summary>
public class CabinetAppService_Tests : VaultExtractApplicationTestBase<CabinetAppServiceTestModule>
{
    private readonly ICabinetAppService _appService;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDocumentRepository _documentRepository;

    public CabinetAppService_Tests()
    {
        _appService = GetRequiredService<ICabinetAppService>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task DeleteAsync_Unfiles_All_Referencing_Documents_Before_Removing_Cabinet()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        _cabinetRepository.GetAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);

        _documentRepository.UnassignCabinetDocumentsAsync(cabinet.Id, Arg.Any<CancellationToken>())
            .Returns(2);

        await _appService.DeleteAsync(cabinet.Id);

        // The repository owns the set-based reconciliation. Its EF integration tests pin the affected
        // live/recycle-bin rows; this application test pins ordering before the cabinet deletion.
        await _documentRepository.Received(1).UnassignCabinetDocumentsAsync(
            cabinet.Id, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _documentRepository.UnassignCabinetDocumentsAsync(cabinet.Id, Arg.Any<CancellationToken>());
            _cabinetRepository.DeleteAsync(cabinet, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        });
        await _cabinetRepository.Received(1).DeleteAsync(cabinet, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Skips_Unfile_When_No_Documents_Reference_Cabinet()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Empty");
        _cabinetRepository.GetAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);

        _documentRepository.UnassignCabinetDocumentsAsync(cabinet.Id, Arg.Any<CancellationToken>())
            .Returns(0);

        await _appService.DeleteAsync(cabinet.Id);

        await _documentRepository.Received(1).UnassignCabinetDocumentsAsync(
            cabinet.Id, Arg.Any<CancellationToken>());
        await _cabinetRepository.Received(1).DeleteAsync(cabinet, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_EntityNotFound_For_Cross_Layer_Cabinet()
    {
        // Cross-layer defense: current layer (Host, CurrentTenant.Id IS NULL) tries to delete a tenant
        // cabinet -> EntityNotFound, and nothing is deleted.
        var tenantCabinet = new Cabinet(Guid.NewGuid(), Guid.NewGuid(), "TenantOwned");
        _cabinetRepository.GetAsync(tenantCabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(tenantCabinet);

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
            await _appService.DeleteAsync(tenantCabinet.Id));

        await _documentRepository.DidNotReceive().UnassignCabinetDocumentsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _cabinetRepository.DidNotReceive().DeleteAsync(
            Arg.Any<Cabinet>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}

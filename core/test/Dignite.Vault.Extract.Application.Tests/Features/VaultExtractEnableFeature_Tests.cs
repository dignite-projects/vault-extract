using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Features;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Features;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class VaultExtractEnableFeatureTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// <see cref="VaultExtractFeatures.Enable"/> gates the application services and nothing that runs the pipeline.
/// The pipeline half lives next to its subject - <c>DocumentReadyEventHandler_Tests</c> - and the structural half
/// in <see cref="VaultExtractAppServiceFeature_Tests"/>.
/// </summary>
public class VaultExtractEnableFeature_Tests : VaultExtractApplicationTestBase<VaultExtractEnableFeatureTestModule>
{
    private readonly IFeatureDefinitionManager _featureDefinitionManager;
    private readonly IFeatureChecker _featureChecker;
    private readonly TestFeatureValueProvider _testFeatures;
    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly ICabinetAppService _cabinetAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _distributedEventBus;

    public VaultExtractEnableFeature_Tests()
    {
        _featureDefinitionManager = GetRequiredService<IFeatureDefinitionManager>();
        _featureChecker = GetRequiredService<IFeatureChecker>();
        _testFeatures = GetRequiredService<TestFeatureValueProvider>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _cabinetAppService = GetRequiredService<ICabinetAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Should_Be_Enabled_By_Default()
    {
        // A host without the Feature Management module has no way to switch this on, so "off unless granted"
        // would lock it out of every application service.
        (await _featureChecker.IsEnabledAsync(VaultExtractFeatures.Enable)).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Be_Visible_To_Clients_And_Not_Locked_To_A_Provider()
    {
        var definition = await _featureDefinitionManager.GetOrNullAsync(VaultExtractFeatures.Enable);

        definition.ShouldNotBeNull();
        definition!.DefaultValue.ShouldBe("true");
        // The Angular SPA can read it from application-configuration to hide its menu.
        definition.IsVisibleToClients.ShouldBeTrue();
        // An empty list allows every provider: tenant and edition values (and the test override) all apply.
        definition.AllowedProviders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_Honour_A_Value_From_A_Higher_Provider()
    {
        _testFeatures.Set(VaultExtractFeatures.Enable, "false");

        (await _featureChecker.IsEnabledAsync(VaultExtractFeatures.Enable)).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Serve_The_Application_Services_While_The_Feature_Is_Enabled()
    {
        var document = CreateDocument();
        _documentRepository.GetAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(document);

        await _documentAppService.DeleteAsync(document.Id);

        await _documentRepository.Received(1).DeleteAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Refuse_A_Write_And_Run_None_Of_It_When_The_Feature_Is_Disabled()
    {
        _testFeatures.Set(VaultExtractFeatures.Enable, "false");
        var document = CreateDocument();
        _documentRepository.GetAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(document);

        var exception = await Should.ThrowAsync<AbpAuthorizationException>(
            () => _documentAppService.DeleteAsync(document.Id));

        // The feature gate and not some other refusal: this host allows every permission.
        exception.Code.ShouldBe(AbpFeatureErrorCodes.AtLeastOneOfTheseFeaturesMustBeEnabled);
        await _documentRepository.DidNotReceiveWithAnyArgs()
            .DeleteAsync(default(Guid), default, default);
        await _distributedEventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentDeletedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Refuse_The_Configuration_Services_Too_When_The_Feature_Is_Disabled()
    {
        _testFeatures.Set(VaultExtractFeatures.Enable, "false");

        // One service of each family that is not DocumentAppService, so the gate is shown to come from the
        // base class and not from anything in the documents domain.
        (await Should.ThrowAsync<AbpAuthorizationException>(
            () => _documentTypeAppService.GetVisibleSummariesAsync()))
            .Code.ShouldBe(AbpFeatureErrorCodes.AtLeastOneOfTheseFeaturesMustBeEnabled);
        (await Should.ThrowAsync<AbpAuthorizationException>(
            () => _cabinetAppService.GetListAsync()))
            .Code.ShouldBe(AbpFeatureErrorCodes.AtLeastOneOfTheseFeaturesMustBeEnabled);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

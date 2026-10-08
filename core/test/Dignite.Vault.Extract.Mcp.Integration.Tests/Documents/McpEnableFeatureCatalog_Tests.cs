using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Features;
using Dignite.Vault.Extract.Permissions;
using ModelContextProtocol.Protocol;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>Stands in for a downstream category: always granted, and it never touches an Extract service.</summary>
public sealed class DownstreamLedgerResourceListContributor : IMcpResourceListContributor, ITransientDependency
{
    public const string LedgerUri = "vault-extract://downstream/ledger";

    public Task<IList<Resource>?> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IList<Resource>?>(new List<Resource> { new() { Uri = LedgerUri, Name = "ledger" } });
    }
}

[DependsOn(typeof(McpPermissionPipelineTestModule), typeof(VaultExtractMcpModule))]
public class McpEnableFeatureCatalogTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<VaultExtractMcpOptions>(options =>
        {
            options.ResourceListContributors.Add<DownstreamLedgerResourceListContributor>();
        });
    }
}

/// <summary>
/// resources/list with <see cref="VaultExtractFeatures.Enable"/> off, against the real permission pipeline and the
/// real (gated) application services. The caller holds the permission, so the built-in cabinet category gets past
/// its own check and is refused by <c>ICabinetReadAppService</c>; that must read as a denied category, not abort the
/// listing and hide the downstream category registered after it.
/// </summary>
public class McpEnableFeatureCatalog_Tests : McpPermissionPipelineTestBase<McpEnableFeatureCatalogTestModule>
{
    private const string UserProviderName = "U";

    private static readonly Guid ServiceAccountId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly IMcpResourceCatalog _catalog;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly TestFeatureValueProvider _testFeatures;

    public McpEnableFeatureCatalog_Tests()
    {
        _catalog = GetRequiredService<IMcpResourceCatalog>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _testFeatures = GetRequiredService<TestFeatureValueProvider>();
    }

    [Fact]
    public async Task Lists_the_cabinet_and_the_downstream_category_while_the_feature_is_enabled()
    {
        var cabinetId = await SeedAsync();

        using (_principalAccessor.Change(ServiceAccountPrincipal()))
        {
            var uris = await ListUrisAsync();

            uris.ShouldBe(new[] { CabinetResourceUri.Format(cabinetId), DownstreamLedgerResourceListContributor.LedgerUri });
        }
    }

    [Fact]
    public async Task Lists_the_downstream_category_when_the_feature_is_disabled()
    {
        await SeedAsync();
        _testFeatures.Set(VaultExtractFeatures.Enable, "false");

        using (_principalAccessor.Change(ServiceAccountPrincipal()))
        {
            var uris = await ListUrisAsync();

            uris.ShouldBe(new[] { DownstreamLedgerResourceListContributor.LedgerUri });
        }
    }

    private async Task<List<string>> ListUrisAsync()
    {
        List<string> uris = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            uris = (await _catalog.ListVisibleAsync()).Resources.Select(resource => resource.Uri).ToList();
        });
        return uris;
    }

    private async Task<Guid> SeedAsync()
    {
        var cabinetId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _cabinetRepository.InsertAsync(
                new Cabinet(cabinetId, tenantId: null, "Legal", "Contracts"), autoSave: true);
            await _permissionGrantRepository.InsertAsync(
                new PermissionGrant(
                    _guidGenerator.Create(),
                    VaultExtractPermissions.Documents.Default,
                    UserProviderName,
                    ServiceAccountId.ToString(),
                    tenantId: null),
                autoSave: true);
        });
        return cabinetId;
    }

    private static ClaimsPrincipal ServiceAccountPrincipal()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(AbpClaimTypes.UserId, ServiceAccountId.ToString()) },
            authenticationType: "IntegrationTest");
        return new ClaimsPrincipal(identity);
    }
}

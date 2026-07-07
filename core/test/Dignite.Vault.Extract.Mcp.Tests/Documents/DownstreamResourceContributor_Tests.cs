using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Stands in for a downstream (e.g. commercial-edition) resource category. Always granted so the test
/// controls visibility purely through the built-in categories' permission set.
/// </summary>
public sealed class FakeLedgerResourceListContributor : IMcpResourceListContributor, ITransientDependency
{
    public const string LedgerUri = "vault-commercial://ledgers/general";

    public Task<IList<Resource>?> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IList<Resource>?>(new List<Resource>
        {
            new() { Uri = LedgerUri, Name = "general" }
        });
    }
}

[DependsOn(typeof(McpResourceCatalogTestModule))]
public class DownstreamResourceContributorTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The documented downstream seam: append a category after the built-in ones, without touching
        // VaultExtractMcpModule.
        Configure<VaultExtractMcpOptions>(options =>
        {
            options.ResourceListContributors.Add<FakeLedgerResourceListContributor>();
        });
    }
}

/// <summary>
/// Guards the resources/list extension seam: a downstream module appends its own
/// <see cref="IMcpResourceListContributor"/> via <see cref="VaultExtractMcpOptions"/> and its category
/// composes after the built-in ones, while the built-in categories' own authorization gating is
/// unaffected.
/// </summary>
public class DownstreamResourceContributor_Tests : VaultExtractTestBase<DownstreamResourceContributorTestModule>
{
    private readonly McpCatalogGrantAuthorizationService _authorization;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IMcpResourceCatalog _catalog;

    public DownstreamResourceContributor_Tests()
    {
        _authorization = GetRequiredService<McpCatalogGrantAuthorizationService>();
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _catalog = GetRequiredService<IMcpResourceCatalog>();
    }

    [Fact]
    public async Task Downstream_category_composes_after_built_in_categories()
    {
        _authorization.Granted = new HashSet<string>
        {
            VaultExtractPermissions.DocumentTypes.Default
        };
        _documentTypeAppService.GetVisibleAsync().Returns(new List<DocumentTypeDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TypeCode = "contract.general",
                DisplayName = "Contract"
            }
        });

        var result = await _catalog.ListVisibleAsync();

        result.Resources.Count.ShouldBe(2);
        result.Resources[0].Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        result.Resources[1].Uri.ShouldBe(FakeLedgerResourceListContributor.LedgerUri);
    }

    [Fact]
    public async Task Downstream_grant_alone_lists_only_the_downstream_category()
    {
        // No built-in permission granted: built-in contributors return null and are skipped, the
        // always-granted downstream category still satisfies the fail-closed gate, and no built-in
        // AppService is consulted.
        _authorization.Granted.Clear();

        var result = await _catalog.ListVisibleAsync();

        result.Resources.Count.ShouldBe(1);
        result.Resources[0].Uri.ShouldBe(FakeLedgerResourceListContributor.LedgerUri);
        await _documentTypeAppService.DidNotReceive().GetVisibleAsync();
    }
}

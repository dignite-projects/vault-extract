using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Xunit;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// The adapter between this module's resources/list catalog and the shared MCP server's listing.
/// </summary>
public class VaultExtractMcpResourceListContributor_Tests
{
    [Fact]
    public async Task Passes_the_catalog_listing_through()
    {
        var catalog = Substitute.For<IMcpResourceCatalog>();
        catalog.ListVisibleAsync(Arg.Any<CancellationToken>()).Returns(new ListResourcesResult
        {
            Resources = new List<Resource> { new() { Uri = "vault-extract://document-types/invoice", Name = "invoice" } }
        });

        var resources = await new VaultExtractMcpResourceListContributor(catalog).ListAsync(CancellationToken.None);

        resources.ShouldNotBeNull().ShouldHaveSingleItem().Uri.ShouldBe("vault-extract://document-types/invoice");
    }

    /// <summary>
    /// The catalog still refuses outright when every category is denied, but on a shared server that refusal
    /// would hide other modules' resources too - so it becomes "nothing from this module".
    /// </summary>
    [Fact]
    public async Task Turns_an_all_categories_denied_refusal_into_no_contribution()
    {
        var catalog = Substitute.For<IMcpResourceCatalog>();
        catalog.ListVisibleAsync(Arg.Any<CancellationToken>()).Returns<Task<ListResourcesResult>>(_ => throw new AbpAuthorizationException());

        var resources = await new VaultExtractMcpResourceListContributor(catalog).ListAsync(CancellationToken.None);

        resources.ShouldBeNull();
    }
}

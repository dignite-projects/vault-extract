using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.AspNetCore.Mcp;
using ModelContextProtocol.Protocol;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Hands this module's resources/list catalog (<see cref="IMcpResourceCatalog"/>, composed from
/// <see cref="VaultExtractMcpOptions.ResourceListContributors"/>) to the shared MCP server's listing.
/// <para>
/// <b>A caller granted none of the categories gets an empty contribution, not a refusal.</b> The catalog
/// reports that case as <see cref="AbpAuthorizationException"/>, which was right while this module owned
/// the whole of resources/list. On a server shared with other modules, refusing the listing would also
/// hide every other module's resources from a caller who may read those.
/// </para>
/// </summary>
public class VaultExtractMcpResourceListContributor : IAbpMcpResourceListContributor, ITransientDependency
{
    protected IMcpResourceCatalog Catalog { get; }

    public VaultExtractMcpResourceListContributor(IMcpResourceCatalog catalog)
    {
        Catalog = catalog;
    }

    public virtual async Task<IReadOnlyCollection<Resource>?> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await Catalog.ListVisibleAsync(cancellationToken)).Resources.ToList();
        }
        catch (AbpAuthorizationException)
        {
            return null;
        }
    }
}

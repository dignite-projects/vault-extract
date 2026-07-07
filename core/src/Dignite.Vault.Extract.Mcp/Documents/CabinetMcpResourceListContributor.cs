using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Built-in resources/list category for cabinets. Granted by Documents.Default (a document reader needs
/// cabinet ids to scope the search tool) or Cabinets.Default; projection and the hard result cap stay in
/// <see cref="CabinetResources.ListVisibleAsync"/>.
/// </summary>
public class CabinetMcpResourceListContributor : IMcpResourceListContributor, ITransientDependency
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ICabinetReadAppService _cabinetReadAppService;

    public CabinetMcpResourceListContributor(
        IAuthorizationService authorizationService,
        ICabinetReadAppService cabinetReadAppService)
    {
        _authorizationService = authorizationService;
        _cabinetReadAppService = cabinetReadAppService;
    }

    public virtual async Task<IList<Resource>?> ListAsync(CancellationToken cancellationToken = default)
    {
        var granted =
            await _authorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default) ||
            await _authorizationService.IsGrantedAsync(VaultExtractPermissions.Cabinets.Default);
        if (!granted)
        {
            return null;
        }

        var result = await CabinetResources.ListVisibleAsync(_cabinetReadAppService);
        return result.Resources;
    }
}

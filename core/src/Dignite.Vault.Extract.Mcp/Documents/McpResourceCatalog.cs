using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Composes resources/list without making independently authorized resource categories cross-gate
/// each other. Each delegated AppService still repeats its own fail-closed authorization assertion.
/// </summary>
public static class McpResourceCatalog
{
    public static async Task<ListResourcesResult> ListVisibleAsync(IServiceProvider services)
    {
        var authorizationService = services.GetRequiredService<IAuthorizationService>();
        var canReadDocuments = await authorizationService.IsGrantedAsync(
            VaultExtractPermissions.Documents.Default);
        var canReadDocumentTypes = canReadDocuments || await authorizationService.IsGrantedAsync(
            VaultExtractPermissions.DocumentTypes.Default);
        var canReadCabinets = canReadDocuments || await authorizationService.IsGrantedAsync(
            VaultExtractPermissions.Cabinets.Default);

        if (!canReadDocumentTypes && !canReadCabinets)
        {
            throw new AbpAuthorizationException();
        }

        var resources = new List<Resource>();
        if (canReadDocumentTypes)
        {
            var documentTypeAppService = services.GetRequiredService<IDocumentTypeAppService>();
            var documentTypes = await DocumentTypeResources.ListVisibleAsync(documentTypeAppService);
            resources.AddRange(documentTypes.Resources);
        }

        if (canReadCabinets)
        {
            var cabinetReadAppService = services.GetRequiredService<ICabinetReadAppService>();
            var cabinets = await CabinetResources.ListVisibleAsync(cabinetReadAppService);
            resources.AddRange(cabinets.Resources);
        }

        return new ListResourcesResult { Resources = resources };
    }
}

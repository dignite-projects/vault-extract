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
/// Built-in resources/list category for document types. Granted by Documents.Default (a document reader
/// needs type schemas to drive the search tool) or DocumentTypes.Default; projection, stable ordering,
/// and the hard result cap stay in <see cref="DocumentTypeResources.ListVisibleAsync"/>.
/// </summary>
public class DocumentTypeMcpResourceListContributor : IMcpResourceListContributor, ITransientDependency
{
    private readonly IAuthorizationService _authorizationService;
    private readonly IDocumentTypeAppService _documentTypeAppService;

    public DocumentTypeMcpResourceListContributor(
        IAuthorizationService authorizationService,
        IDocumentTypeAppService documentTypeAppService)
    {
        _authorizationService = authorizationService;
        _documentTypeAppService = documentTypeAppService;
    }

    public virtual async Task<IList<Resource>?> ListAsync(CancellationToken cancellationToken = default)
    {
        var granted =
            await _authorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default) ||
            await _authorizationService.IsGrantedAsync(VaultExtractPermissions.DocumentTypes.Default);
        if (!granted)
        {
            return null;
        }

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);
        return result.Resources;
    }
}

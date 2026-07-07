using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Composes resources/list from the <see cref="IMcpResourceListContributor"/> types registered in
/// <see cref="VaultExtractMcpOptions.ResourceListContributors"/>, without making independently authorized
/// resource categories cross-gate each other: a contributor returning <c>null</c> (category permission
/// not granted) is skipped, and only when every category is denied does the whole call fail closed with
/// <see cref="AbpAuthorizationException"/>. Contributors are resolved from the current (request) scope so
/// authorization sees the calling principal; each delegated AppService still repeats its own fail-closed
/// authorization assertion.
/// </summary>
public class McpResourceCatalog : IMcpResourceCatalog, ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly VaultExtractMcpOptions _options;

    public McpResourceCatalog(IServiceProvider serviceProvider, IOptions<VaultExtractMcpOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    public virtual async Task<ListResourcesResult> ListVisibleAsync(CancellationToken cancellationToken = default)
    {
        var anyCategoryGranted = false;
        var resources = new List<Resource>();
        foreach (var contributorType in _options.ResourceListContributors)
        {
            var contributor = (IMcpResourceListContributor)_serviceProvider.GetRequiredService(contributorType);
            var contributed = await contributor.ListAsync(cancellationToken);
            if (contributed is null)
            {
                continue;
            }

            anyCategoryGranted = true;
            resources.AddRange(contributed);
        }

        if (!anyCategoryGranted)
        {
            throw new AbpAuthorizationException();
        }

        return new ListResourcesResult { Resources = resources };
    }
}

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
/// <para>
/// A contributor whose delegated AppService <b>refuses</b> (throws <see cref="AbpAuthorizationException"/>
/// after the contributor's own permission check passed) is denied the same way as one that returned
/// <c>null</c>: that category is skipped and the rest still list. The case that matters is
/// <c>VaultExtract.Enable</c> being off for the tenant, which refuses every Extract application service
/// whatever the caller's permissions; without this, the built-in categories would abort the loop and hide
/// the downstream categories registered after them.
/// </para>
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
            IList<Resource>? contributed;
            try
            {
                contributed = await contributor.ListAsync(cancellationToken);
            }
            catch (AbpAuthorizationException)
            {
                // Fail closed for this category only; see the type doc.
                continue;
            }

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

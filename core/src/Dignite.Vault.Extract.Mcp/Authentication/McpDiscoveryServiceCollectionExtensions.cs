using System;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// #422: reusable wiring for the #278 MCP OAuth Protected Resource Metadata discovery flow (RFC 9728),
/// exported by the Mcp egress adapter so any host deploying the Vault Extract MCP egress enables it
/// with a single call instead of re-authoring the handler + scheme registration.
///
/// What this method owns (the deployment-agnostic mechanics): registering the McpAuth scheme via
/// <c>AddMcp</c>, hiding it from the ABP Account login page, and replacing the
/// <see cref="IAuthorizationMiddlewareResultHandler"/> with
/// <see cref="McpDiscoveryAuthorizationResultHandler"/> so only challenge — not authentication — is
/// overridden (preserving the principal enriched by ABP <c>UseDynamicClaims</c>; see the handler's
/// comments). What the host still owns: reading deployment configuration (authority / self URL), the
/// decision whether discovery is enabled at all, populating the <see cref="ProtectedResourceMetadata"/>
/// values, and mapping the endpoint via <c>MapMcp("/mcp").WithMetadata(McpDiscoveryChallengeMarker.Instance)</c>.
/// </summary>
public static class McpDiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the McpAuth discovery scheme and directs unauthenticated /mcp challenges to emit the
    /// <c>WWW-Authenticate: Bearer resource_metadata="..."</c> pointer. Caller fills the
    /// <see cref="ProtectedResourceMetadata"/> (resource URI / authorization servers / scopes) from its
    /// own deployment configuration.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configureResourceMetadata">
    /// Populates the RFC 9728 metadata advertised at <c>/.well-known/oauth-protected-resource</c> and in
    /// the 401 challenge pointer. Deployment-specific values stay with the caller.
    /// </param>
    public static IServiceCollection AddExtractMcpDiscovery(
        this IServiceCollection services,
        Action<ProtectedResourceMetadata> configureResourceMetadata)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureResourceMetadata);

        services.AddAuthentication().AddMcp(options =>
        {
            var metadata = new ProtectedResourceMetadata();
            configureResourceMetadata(metadata);
            options.ResourceMetadata = metadata;
        });

        // McpAuth is used only for /.well-known/oauth-protected-resource self-service and /mcp endpoint
        // 401 challenge. It is not a user-interactive external login provider. Clear DisplayName so the
        // ABP Account module will not render it as a login-page button.
        services.Configure<AuthenticationOptions>(options =>
        {
            var scheme = options.Schemes.FirstOrDefault(s => s.Name == McpAuthenticationDefaults.AuthenticationScheme);
            if (scheme != null)
                scheme.DisplayName = null;
        });

        // Override only challenge, not authenticate: preserve the principal enriched by ABP dynamic
        // claims (see McpDiscoveryAuthorizationResultHandler comments).
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, McpDiscoveryAuthorizationResultHandler>());

        return services;
    }
}

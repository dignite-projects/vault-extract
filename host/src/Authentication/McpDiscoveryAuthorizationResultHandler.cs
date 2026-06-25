using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Dignite.Vault.Extract.Host.Authentication;

/// <summary>
/// #278: only for endpoints marked with <see cref="McpDiscoveryChallengeMarker"/> (/mcp), route
/// unauthenticated challenges to the McpAuth scheme so 401 responses carry the
/// <c>WWW-Authenticate: Bearer resource_metadata="..."</c> discovery pointer (RFC 9728). All other
/// endpoints are delegated to the framework default handler.
///
/// Why not add McpAuth directly to the endpoint authorization policy's AuthenticationSchemes: that
/// would make PolicyEvaluator authenticate again through McpAuth -> OpenIddict, producing a principal
/// that has not been enriched by ABP dynamic claims and overwriting the ambient User enriched by
/// UseDynamicClaims. That would lose role changes made after token issuance, potentially rejecting a
/// valid token, and would also bypass real-time invalidation for users revoked / disabled after token
/// issuance, a security regression. This handler only overrides challenge; authentication still uses
/// the endpoint default policy (RequireAuthenticatedUser, no explicit scheme) and reuses the ambient
/// enriched User.
///
/// Also do not change the global DefaultChallengeScheme to McpAuth because that would break cookie
/// login redirects for the admin UI.
/// </summary>
public class McpDiscoveryAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private static readonly AuthorizationMiddlewareResultHandler Default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // Only 401 (unauthenticated) uses McpAuth challenge to inject the discovery pointer; 403
        // (authenticated but insufficient permissions) keeps the framework default behavior.
        if (authorizeResult.Challenged
            && !authorizeResult.Forbidden
            && context.GetEndpoint()?.Metadata.GetMetadata<McpDiscoveryChallengeMarker>() != null)
        {
            await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        await Default.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>
/// Endpoint marker declaring that unauthenticated challenges for this endpoint use the MCP OAuth
/// Protected Resource Metadata discovery flow (#278). Recognized by
/// <see cref="McpDiscoveryAuthorizationResultHandler"/>.
/// </summary>
public sealed class McpDiscoveryChallengeMarker
{
    public static readonly McpDiscoveryChallengeMarker Instance = new();

    private McpDiscoveryChallengeMarker()
    {
    }
}

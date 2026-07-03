using System.Collections.Generic;
using System.Security.Claims;
using Volo.Abp.Security.Claims;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Builds the synthetic authenticated principal a matched API key authenticates as (#428). Kept as a single
/// shared factory so the request middleware and the integration test (#432) construct byte-identical principals —
/// the test's fidelity depends on exercising the <b>same</b> claim shape the runtime produces, not a hand-rolled
/// copy that could drift. A future <c>AuthenticationHandler</c>/scheme upgrade (#431) would reuse this too.
/// </summary>
public static class McpApiKeyPrincipalFactory
{
    /// <summary>
    /// The principal carries only <c>AbpClaimTypes.UserId</c> (+ tenant when configured) and a non-empty
    /// authentication type. Permissions are NOT stamped as claims — ABP resolves them from the permission store
    /// by user id at the tools' <c>CheckPolicyAsync</c>. The non-empty <see cref="McpApiKeyDefaults.AuthenticationType"/>
    /// makes the identity <c>IsAuthenticated == true</c>, which is load-bearing: otherwise <c>RequireAuthorization</c>
    /// 401s and ABP's <c>!IsAuthenticated</c> guard would re-run OpenIddict over it. The key's <c>Label</c> is used
    /// only for log attribution, never stamped as <c>AbpClaimTypes.UserName</c> (that would mislead audit
    /// correlation against the real Identity service-account user).
    /// </summary>
    public static ClaimsPrincipal Create(McpApiKeyEntry entry)
    {
        var claims = new List<Claim>
        {
            new(AbpClaimTypes.UserId, entry.ServiceAccountUserId)
        };

        if (!string.IsNullOrWhiteSpace(entry.TenantId))
        {
            claims.Add(new Claim(AbpClaimTypes.TenantId, entry.TenantId));
        }

        var identity = new ClaimsIdentity(claims, McpApiKeyDefaults.AuthenticationType);
        return new ClaimsPrincipal(identity);
    }
}

using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// #431: the static API-key channel as a real ASP.NET Core <see cref="AuthenticationHandler{TOptions}"/> exposed
/// as the <see cref="McpApiKeyDefaults.AuthenticationScheme"/> scheme, replacing the #430 path-scoped middleware.
///
/// <para>Why a scheme, not middleware. When the host's cookie <c>ForwardDefaultSelector</c> routes a
/// <c>/mcp</c> + key request here, ASP.NET Core's <c>AuthenticationMiddleware</c> records the result as an
/// <see cref="IAuthenticateResultFeature"/>. ABP's <c>UseDynamicClaims</c> (<c>AbpDynamicClaimsMiddleware</c>)
/// only re-enriches / revokes principals that carry that feature (verified against ABP 10.2.0:
/// <c>if (authenticateResultFeature != null …) CreateDynamicAsync(...)</c>). The old middleware set
/// <c>context.User</c> directly with no such feature, so the key principal never got live enrichment or
/// real-time revocation. As a scheme, a valid key now flows through the same dynamic-claims path as a Bearer
/// user — disabling / deleting the mapped service-account user takes effect on the next request.</para>
///
/// <para>Behaviour. On a constant-time match, succeed with the mapped least-privilege service-account principal
/// (<see cref="McpApiKeyPrincipalFactory"/>, the same claim shape the middleware used). On a missing / duplicate
/// / insecure-transport / invalid key, return <see cref="AuthenticateResult.NoResult"/> — never a failure and
/// never a 401/403 written here — so the request falls through to the OpenIddict Bearer chain and the #278
/// discovery challenge stay intact (the endpoint keeps its bare scheme-free <c>RequireAuthorization()</c>).</para>
/// </summary>
public sealed class McpApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly McpApiKeyRegistry _registry;

    public McpApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpApiKeyRegistry registry)
        : base(options, logger, encoder)
    {
        _registry = registry;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Exactly one header instance is expected. Absent (Count == 0) is the normal OAuth path; a duplicated
        // header (Count > 1, e.g. a proxy that appends) is ambiguous. Either way NoResult → fall through.
        var values = Request.Headers[_registry.HeaderName];
        if (values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = values[0];
        if (string.IsNullOrEmpty(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The key is a long-lived bearer-equivalent secret; refuse it over clear text (fall through). Never log
        // the value. Disable via Mcp:ApiKey:RequireHttps only for a deliberate plain-HTTP deployment.
        if (_registry.RequireHttps && !Request.IsHttps)
        {
            Logger.LogDebug(
                "An MCP API key was presented over a non-HTTPS request; ignoring it (Mcp:ApiKey:RequireHttps=true).");
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var matched = _registry.Match(presented);
        if (matched == null)
        {
            _registry.LogInvalidKey(Context);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = McpApiKeyPrincipalFactory.Create(matched);
        Logger.LogDebug(
            "MCP request authenticated via API key (label: {Label}).",
            string.IsNullOrWhiteSpace(matched.Label) ? "<unlabeled>" : matched.Label);

        // Success as this scheme -> AuthenticationMiddleware sets IAuthenticateResultFeature -> UseDynamicClaims
        // re-enriches + can revoke, exactly like a Bearer user.
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}

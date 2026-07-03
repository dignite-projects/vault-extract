using System;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Reusable wiring for the static API-key channel on <c>/mcp</c> (#428), exported by the Mcp egress module.
/// #431 upgraded the channel from a path-scoped middleware to a real ASP.NET Core authentication scheme
/// (<see cref="McpApiKeyDefaults.AuthenticationScheme"/>) so a valid key flows through ABP <c>UseDynamicClaims</c>
/// for live enrichment + real-time revocation. This method registers the deployment-agnostic MECHANISM (options +
/// validation, the singleton <see cref="McpApiKeyRegistry"/>, and the handler/scheme). The HOST still owns the
/// deployment decisions: the config binding and the cookie <c>ForwardDefaultSelector</c> that routes a
/// <c>/mcp</c> + key request to this scheme (it cannot live here because it is Identity/cookie-specific and the
/// Mcp egress module does not depend on Identity).
/// </summary>
public static class McpApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers and fail-fast-validates the API-key options, the singleton match registry, and the
    /// <see cref="McpApiKeyDefaults.AuthenticationScheme"/> authentication scheme. A no-op-at-runtime feature when
    /// no keys are configured (the registry reports <c>IsEnabled == false</c>, so the host's selector never routes
    /// to the scheme). The host wires the forwarding selector; the endpoint keeps its bare
    /// scheme-free <c>RequireAuthorization()</c> (the #278 invariant).
    /// </summary>
    public static IServiceCollection AddVaultExtractMcpApiKey(
        this IServiceCollection services,
        Action<McpApiKeyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Validate eagerly against a throwaway bound instance so misconfiguration fails fast at startup
        // (mirroring ConfigureAI's placeholder guard); a no-op when no keys are configured.
        var validation = new McpApiKeyOptions();
        configure(validation);
        validation.Validate();

        // Register the SAME delegate so every option property binds to the DI instance — avoids a manual
        // field-copy that a future property could silently drift out of.
        services.Configure(configure);

        services.AddSingleton<McpApiKeyRegistry>();

        // Register the scheme unconditionally so the host's ForwardDefaultSelector can always resolve it; when no
        // keys are configured the registry short-circuits (IsEnabled == false) and it is never forwarded to.
        // DisplayName null: this is a machine credential channel, not an interactive login provider, so the ABP
        // Account module must not render it as a login-page button (same as the #422 McpAuth discovery scheme).
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpApiKeyAuthenticationHandler>(
                McpApiKeyDefaults.AuthenticationScheme, displayName: null, configureOptions: null);

        return services;
    }
}

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Constants for the optional static API-key fallback authentication channel on the <c>/mcp</c> egress
/// (#428). The channel complements — never replaces — the OpenIddict Bearer chain and the #278 OAuth
/// discovery flow; it exists for MCP clients that cannot run the dynamic OAuth flow but can send a static
/// request header (OpenAI Codex, ABP AI Management). Claude's native custom connector is OAuth-only and
/// keeps using #278; it is unaffected.
/// </summary>
public static class McpApiKeyDefaults
{
    /// <summary>
    /// Default request header carrying the static key. Configurable via
    /// <see cref="McpApiKeyOptions.HeaderName"/> for operability (reverse-proxy / WAF collisions, matching
    /// a particular client's fixed header name).
    /// </summary>
    public const string DefaultHeaderName = "X-Api-Key";

    /// <summary>Default endpoint path prefix the channel is scoped to (matches the host's <c>MapMcp("/mcp")</c>).</summary>
    public const string DefaultPathPrefix = "/mcp";

    /// <summary>
    /// Name of the ASP.NET Core authentication scheme the API-key handler is registered under (#431). The host's
    /// cookie <c>ForwardDefaultSelector</c> forwards a <c>/mcp</c> + key request to this scheme; the authentication
    /// ticket carries this name, and it equals <see cref="AuthenticationType"/> so the dynamic-claims path sees a
    /// consistent scheme for the key principal.
    /// </summary>
    public const string AuthenticationScheme = "McpApiKey";

    /// <summary>
    /// AuthenticationType stamped on the synthetic principal (same value as <see cref="AuthenticationScheme"/>).
    /// MUST be non-empty so the identity is <c>IsAuthenticated == true</c> — otherwise <c>RequireAuthorization</c>
    /// rejects it (401) and ABP's <c>UseAbpOpenIddictValidation</c> <c>!IsAuthenticated</c> guard would re-run
    /// OpenIddict over it.
    /// </summary>
    public const string AuthenticationType = AuthenticationScheme;

    /// <summary>
    /// Committed-config placeholder, rejected fail-fast at startup (mirroring <c>ConfigureAI</c>'s
    /// <c>YOUR_API_KEY</c> guard) so a real secret must be supplied out-of-band via env / user-secrets.
    /// </summary>
    public const string PlaceholderKey = "YOUR_MCP_API_KEY";

    /// <summary>
    /// Minimum accepted key length. The header check is unauthenticated, so a low-entropy key would be
    /// brute-forceable; keys should be CSPRNG-generated (>= 256 bits, e.g. 32 random bytes base64url).
    /// </summary>
    public const int MinKeyLength = 32;
}

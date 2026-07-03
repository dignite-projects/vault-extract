namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Constants for the <c>/mcp</c> rate limiter (#433). The limiter is a brute-force / log-flood / DoS backstop on
/// the egress endpoint — it caps request volume per client so an unauthenticated caller cannot probe API keys
/// indefinitely or drown the discovery <c>401</c> path, while staying generous enough not to disturb legitimate
/// MCP session traffic (configurable per deployment).
/// </summary>
public static class McpRateLimiterDefaults
{
    /// <summary>
    /// Name of the rate-limiting policy applied to the <c>/mcp</c> endpoint via <c>RequireRateLimiting</c>.
    /// A stable identifier — the host references it when mapping the endpoint.
    /// </summary>
    public const string PolicyName = "VaultExtractMcp";

    /// <summary>Default requests permitted per window, per client partition. Generous; a DoS/brute-force cap, not a quota.</summary>
    public const int DefaultPermitLimit = 300;

    /// <summary>Default fixed-window length in seconds.</summary>
    public const int DefaultWindowSeconds = 60;
}

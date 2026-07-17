using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Controls whether the host may restore the originating client IP from a reverse proxy for
/// <c>/mcp</c> rate-limit partitioning. Forwarded client IPs are disabled by default and, when
/// enabled, require an explicit proxy/network allowlist so arbitrary clients cannot spoof their
/// partition through <c>X-Forwarded-For</c>.
/// </summary>
public sealed class McpForwardedClientIpOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of forwarded entries consumed from the right-hand side of the header.
    /// Keep this aligned with the number of trusted proxy hops in front of the host.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Exact IP addresses of trusted reverse proxies.
    /// </summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// Trusted reverse-proxy networks in CIDR notation.
    /// </summary>
    public List<string> KnownNetworks { get; set; } = [];
}

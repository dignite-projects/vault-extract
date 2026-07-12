using Volo.Abp;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Options for the <c>/mcp</c> rate limiter (#433). Bound from <c>Mcp:RateLimit</c> in the host. Enabled by
/// default with generous limits so it is a security backstop rather than a throughput quota; a deployment with
/// unusual traffic can widen the window / limit or disable it entirely.
/// </summary>
public class McpRateLimiterOptions
{
    /// <summary>
    /// When <c>false</c>, the policy is still registered (so the endpoint's <c>RequireRateLimiting</c> resolves)
    /// but applies no limit. Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Requests permitted per <see cref="WindowSeconds"/> window, per client partition (source IP).</summary>
    public int PermitLimit { get; set; } = McpRateLimiterDefaults.DefaultPermitLimit;

    /// <summary>Fixed-window length in seconds.</summary>
    public int WindowSeconds { get; set; } = McpRateLimiterDefaults.DefaultWindowSeconds;

    /// <summary>
    /// Requests queued when the window is exhausted (they wait for the next window rather than being rejected).
    /// Default <c>0</c> — reject immediately with <c>429</c>, the right shape for a brute-force backstop.
    /// </summary>
    public int QueueLimit { get; set; } = 0;

    /// <summary>Fail-fast shape validation at startup. A no-op when disabled.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (PermitLimit <= 0)
        {
            throw new AbpException("Mcp:RateLimit:PermitLimit must be greater than 0 (or set Mcp:RateLimit:Enabled=false to disable).");
        }

        if (WindowSeconds <= 0)
        {
            throw new AbpException("Mcp:RateLimit:WindowSeconds must be greater than 0.");
        }

        if (QueueLimit < 0)
        {
            throw new AbpException("Mcp:RateLimit:QueueLimit must be greater than or equal to 0.");
        }
    }
}

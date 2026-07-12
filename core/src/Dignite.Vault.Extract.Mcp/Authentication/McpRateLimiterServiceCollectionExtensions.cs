using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Reusable registration for the <c>/mcp</c> rate limiter (#433), exported by the Mcp egress module so any host
/// enables it with one <c>Add</c> call + <c>app.UseRateLimiter()</c> + <c>RequireRateLimiting</c> on the endpoint.
/// The deployment-agnostic MECHANISM (the policy, the per-IP partition, the <c>429</c> rejection) lives here;
/// the HOST owns the config and the pipeline wiring.
/// The policy is <b>always</b> registered under <see cref="McpRateLimiterDefaults.PolicyName"/> so the endpoint's
/// <c>RequireRateLimiting</c> resolves even when the limiter is disabled (it then applies no limit).
/// </summary>
public static class McpRateLimiterServiceCollectionExtensions
{
    public static IServiceCollection AddVaultExtractMcpRateLimiter(
        this IServiceCollection services,
        Action<McpRateLimiterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new McpRateLimiterOptions();
        configure(options);
        options.Validate();

        services.AddRateLimiter(limiter =>
        {
            // 429 (not the framework-default 503): "too many requests" is the honest signal to a probing client
            // and does not read as a server fault. It never fires for legitimate, in-limit traffic.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(McpRateLimiterDefaults.PolicyName, httpContext =>
            {
                if (!options.Enabled)
                {
                    // Policy present but inert: the endpoint stays mapped with RequireRateLimiting, no throttling.
                    return RateLimitPartition.GetNoLimiter(McpRateLimiterDefaults.PolicyName);
                }

                // Partition per source IP so one abusive client cannot exhaust another's budget, and so a flood
                // from a single origin is contained. Unknown IPs share one partition. Behind a reverse proxy this
                // is only as accurate as the host's forwarded-headers handling (document proxy setup).
                var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.PermitLimit,
                        Window = TimeSpan.FromSeconds(options.WindowSeconds),
                        QueueLimit = options.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            });
        });

        return services;
    }
}

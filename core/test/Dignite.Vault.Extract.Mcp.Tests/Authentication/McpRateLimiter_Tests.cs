using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// #433 guard for the /mcp rate limiter. A minimal TestServer mirrors the host wiring
/// (<c>UseRateLimiter</c> after routing + <c>RequireRateLimiting</c> on the /mcp endpoint) and locks:
///  - over-limit requests get 429 while in-limit ones pass (the brute-force / DoS backstop actually fires);
///  - a non-/mcp endpoint without the policy is never limited (scoping);
///  - a disabled limiter applies no limit (policy still registered so RequireRateLimiting resolves).
/// </summary>
public class McpRateLimiter_Tests
{
    [Fact]
    public async Task Requests_over_the_limit_get_429_while_in_limit_requests_pass()
    {
        using var server = await BuildServerAsync(permitLimit: 2);
        using var client = server.CreateClient();

        (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Third request in the same window exceeds PermitLimit=2 -> rejected with 429 (QueueLimit=0).
        (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_non_mcp_endpoint_without_the_policy_is_not_limited()
    {
        using var server = await BuildServerAsync(permitLimit: 2);
        using var client = server.CreateClient();

        // Far more than PermitLimit, none rejected: the limiter is scoped to endpoints that opt in.
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/other")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_disabled_limiter_applies_no_limit()
    {
        using var server = await BuildServerAsync(permitLimit: 2, enabled: false);
        using var client = server.CreateClient();

        // RequireRateLimiting still resolves (policy registered as a no-limiter), but nothing is throttled.
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task<TestServer> BuildServerAsync(int permitLimit, bool enabled = true)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddVaultExtractMcpRateLimiter(options =>
                    {
                        options.Enabled = enabled;
                        options.PermitLimit = permitLimit;
                        options.WindowSeconds = 60;
                        options.QueueLimit = 0;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints
                            .MapGet("/mcp", (HttpContext _) => "ok")
                            .RequireRateLimiting(McpRateLimiterDefaults.PolicyName);

                        endpoints.MapGet("/other", (HttpContext _) => "ok");
                    });
                });
            })
            .StartAsync();

        return host.GetTestServer();
    }
}

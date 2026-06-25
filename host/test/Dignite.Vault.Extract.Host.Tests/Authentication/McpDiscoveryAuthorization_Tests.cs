using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Host.Authentication;

/// <summary>
/// #278 regression guard: locks down the implicit constraint explaining why /mcp must use a scheme-free
/// authorization policy plus <see cref="McpDiscoveryAuthorizationResultHandler"/> directed challenge,
/// instead of attaching McpAuth directly to the policy's AuthenticationSchemes.
///
/// Uses a minimal ASP.NET pipeline (TestServer) to mirror the host MCP wiring precisely: default token
/// scheme, an enrichment middleware that mimics ABP UseDynamicClaims by rewriting the authentication
/// result to a principal with stage=enriched, the real <see cref="McpDiscoveryAuthorizationResultHandler"/>
/// / <see cref="McpDiscoveryChallengeMarker"/>, and SDK AddMcp. No ABP host, DB, or OpenIddict is used.
///
/// Key comparison (<see cref="Authenticated_request_keeps_dynamic_claims_enriched_principal"/> vs
/// <see cref="Explicit_scheme_policy_drops_enrichment_documents_the_regression"/>): scheme-free policy
/// lets the endpoint see the enriched principal; explicitly attaching a scheme makes PolicyEvaluator
/// re-authenticate, overwrite context.User, and fall back to raw. If a future maintainer "simplifies" by
/// putting McpAuth back on the policy, the first test turns red.
/// </summary>
public class McpDiscoveryAuthorization_Tests
{
    private const string TokenScheme = "Token";
    private const string EnrichedClaim = "enriched";
    private const string RawClaim = "raw";

    [Fact]
    public async Task Authenticated_request_keeps_dynamic_claims_enriched_principal()
    {
        using var server = await BuildServerAsync(withDiscovery: true, useExplicitScheme: false);
        using var client = server.CreateClient();

        var response = await WithTokenAsync(client).GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Scheme-free policy: authorization reads the UseDynamicClaims-enriched ambient principal without
        // re-authenticating.
        (await response.Content.ReadAsStringAsync()).ShouldBe(EnrichedClaim);
    }

    [Fact]
    public async Task Unauthenticated_request_gets_401_with_resource_metadata_pointer()
    {
        using var server = await BuildServerAsync(withDiscovery: true, useExplicitScheme: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // McpDiscoveryAuthorizationResultHandler directs challenge to McpAuth and injects the RFC 9728
        // discovery pointer.
        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        wwwAuth.ShouldContain("resource_metadata");
        wwwAuth.ShouldContain("/.well-known/oauth-protected-resource");
    }

    [Fact]
    public async Task Explicit_scheme_policy_drops_enrichment_documents_the_regression()
    {
        // Counterexample: attach the token scheme to policy AuthenticationSchemes, the rejected "simple"
        // version.
        using var server = await BuildServerAsync(withDiscovery: false, useExplicitScheme: true);
        using var client = server.CreateClient();

        var response = await WithTokenAsync(client).GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Because of the explicit scheme, PolicyEvaluator re-authenticates, overwrites context.User, loses
        // enrichment, and falls back to raw.
        (await response.Content.ReadAsStringAsync()).ShouldBe(RawClaim);
    }

    private static HttpClient WithTokenAsync(HttpClient client)
    {
        // StubTokenHandler only checks that the Authorization header exists and does not validate content.
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");
        return client;
    }

    private static async Task<TestServer> BuildServerAsync(bool withDiscovery, bool useExplicitScheme)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();

                    var auth = services
                        .AddAuthentication(TokenScheme)
                        .AddScheme<AuthenticationSchemeOptions, StubTokenHandler>(TokenScheme, null);

                    if (withDiscovery)
                    {
                        auth.AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata
                        {
                            AuthorizationServers = new List<string> { "https://auth.example/" },
                            ScopesSupported = new List<string> { "Extract" },
                            BearerMethodsSupported = new List<string> { "header" }
                        });
                    }

                    services.AddAuthorization();

                    if (withDiscovery)
                    {
                        services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, McpDiscoveryAuthorizationResultHandler>());
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();

                    // Mimics ABP UseDynamicClaims: after authentication, rewrite IAuthenticateResultFeature
                    // to an enriched principal.
                    app.Use(async (ctx, next) =>
                    {
                        if (ctx.User.Identity?.IsAuthenticated == true)
                        {
                            var feature = ctx.Features.Get<IAuthenticateResultFeature>();
                            if (feature?.AuthenticateResult is not null)
                            {
                                var identity = new ClaimsIdentity(TokenScheme);
                                identity.AddClaim(new Claim("stage", EnrichedClaim));
                                feature.AuthenticateResult = AuthenticateResult.Success(
                                    new AuthenticationTicket(new ClaimsPrincipal(identity), TokenScheme));
                            }
                        }

                        await next();
                    });

                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        var endpoint = endpoints.MapGet(
                            "/mcp",
                            (HttpContext context) => context.User.FindFirst("stage")?.Value ?? "none");

                        if (useExplicitScheme)
                        {
                            endpoint.RequireAuthorization(policy => policy
                                .RequireAuthenticatedUser()
                                .AddAuthenticationSchemes(TokenScheme));
                        }
                        else
                        {
                            endpoint.RequireAuthorization();
                        }

                        if (withDiscovery)
                        {
                            endpoint.WithMetadata(McpDiscoveryChallengeMarker.Instance);
                        }
                    });
                });
            })
            .StartAsync();

        return host.GetTestServer();
    }

    // Placeholder token scheme: Authorization header means authentication succeeds with a raw principal;
    // otherwise NoResult triggers challenge. This corresponds to OpenIddict validation reparsing an
    // unenriched principal from the bearer token.
    private sealed class StubTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StubTokenHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(TokenScheme);
            identity.AddClaim(new Claim("stage", RawClaim));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TokenScheme)));
        }
    }
}

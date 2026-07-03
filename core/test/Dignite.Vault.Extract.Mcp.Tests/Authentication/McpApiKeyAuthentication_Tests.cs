using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// #428 / #431 guard for the static API-key channel on /mcp. Uses a minimal TestServer pipeline (no ABP host /
/// DB / OpenIddict) that mirrors the host seam: a default <b>policy scheme</b> whose <c>ForwardDefaultSelector</c>
/// routes a /mcp + key request to the <see cref="McpApiKeyDefaults.AuthenticationScheme"/> scheme (Bearer wins
/// when present), a stub Bearer scheme as the fallback, and the bare scheme-free <c>RequireAuthorization()</c> the
/// host uses on /mcp.
///
/// The load-bearing behaviours locked here:
///  - valid key => the request reaches the endpoint as the mapped service-account principal;
///  - missing key => 401 (Bearer chain + #278 discovery untouched); invalid key => 401, never 403;
///  - the channel is segment-scoped to /mcp and does not authenticate other paths;
///  - a valid Bearer still wins when both are sent;
///  - <b>#431</b>: a valid key yields an <see cref="IAuthenticateResultFeature"/> (scheme = McpApiKey), so the key
///    principal flows through the same re-enrichment / revocation seam ABP's <c>UseDynamicClaims</c> uses — a
///    dynamic-claims-style middleware can revoke a disabled user's key (the old middleware principal could not).
/// </summary>
public class McpApiKeyAuthentication_Tests
{
    private const string DefaultScheme = "Default";
    private const string TokenScheme = "Token";
    private const string RawClaim = "raw";
    private const string HeaderName = "X-Api-Key";

    // >= McpApiKeyDefaults.MinKeyLength (32). Test-only values, not real secrets.
    private const string ValidKey = "test-mcp-api-key-0123456789abcdefghijklmnop";
    private const string SecondKey = "second-mcp-api-key-zyxwvutsrqponmlkjihgfedcba";
    // #435: presented in plaintext but configured only as its SHA-256 digest (KeyHash).
    private const string HashOnlyKey = "hash-only-mcp-api-key-abcdefghijklmnopqrstuvwx";

    private static readonly Guid ServiceAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HashAccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Valid_api_key_authenticates_as_the_mapped_service_account()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(ServiceAccountId.ToString());
    }

    [Fact]
    public async Task Missing_api_key_falls_through_to_401()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_api_key_falls_through_to_401_not_403()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, "wrong-but-long-enough-key-aaaaaaaaaaaaaaaa");

        var response = await client.GetAsync("/mcp");

        // 401 (unauthenticated challenge), NOT 403 — a 403 would make the #278 discovery handler skip the
        // resource_metadata pointer and break OAuth-client discovery.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_channel_is_scoped_to_the_mcp_path()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        // Same valid key on a non-/mcp protected path: the selector does not route it to the key scheme.
        var response = await client.GetAsync("/other");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_second_configured_key_authenticates_as_its_own_service_account()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, SecondKey);

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(SecondAccountId.ToString());
    }

    [Fact]
    public async Task A_valid_bearer_wins_when_both_a_key_and_a_bearer_are_sent()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The selector routes a Bearer-bearing request to the Bearer scheme, so the Bearer principal wins.
        (await response.Content.ReadAsStringAsync()).ShouldBe(RawClaim);
    }

    [Fact]
    public async Task Valid_bearer_still_authenticates_when_no_api_key_is_sent()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(RawClaim);
    }

    [Fact]
    public async Task A_valid_key_over_plain_http_is_rejected_when_https_is_required()
    {
        using var server = await BuildServerAsync(requireHttps: true);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        // TestServer serves plain HTTP, so the handler's RequireHttps gate ignores the key -> NoResult -> 401.
        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- #431: dynamic-claims eligibility + real-time revocation parity ----

    [Fact]
    public async Task A_valid_key_exposes_an_authenticate_result_feature_for_dynamic_claims()
    {
        // The whole point of #431 over the #430 middleware: the key principal carries an IAuthenticateResultFeature
        // (scheme = McpApiKey), which is exactly what AbpDynamicClaimsMiddleware requires to re-enrich / revoke it.
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        var response = await client.GetAsync("/mcp/authresult");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(McpApiKeyDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task A_revoked_service_account_key_is_blocked_by_the_dynamic_claims_seam()
    {
        // A dynamic-claims-style middleware (mirroring AbpDynamicClaimsMiddleware: read IAuthenticateResultFeature,
        // produce an unauthenticated principal for a revoked user) blocks the key on the next request — the
        // revocation parity the middleware approach could not achieve because its principal had no such feature.
        using var server = await BuildServerAsync(revokedUserId: ServiceAccountId);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_non_revoked_key_still_authenticates_through_the_dynamic_claims_seam()
    {
        // Control: with the revocation seam present but this account NOT revoked, the key still authenticates.
        using var server = await BuildServerAsync(revokedUserId: SecondAccountId);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(ServiceAccountId.ToString());
    }

    // ---- #435: hash-at-rest (KeyHash) ----

    [Fact]
    public async Task A_keyhash_configured_key_authenticates_when_the_plaintext_is_presented()
    {
        using var server = await BuildServerAsync(customize: options => options.Keys.Add(new McpApiKeyEntry
        {
            KeyHash = McpApiKeyHasher.ComputeSha256Hex(HashOnlyKey),
            ServiceAccountUserId = HashAccountId.ToString(),
            Label = "hash-test"
        }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, HashOnlyKey);

        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(HashAccountId.ToString());
    }

    [Fact]
    public void Configuration_setting_both_key_and_keyhash_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry>
            {
                new()
                {
                    Key = ValidKey,
                    KeyHash = McpApiKeyHasher.ComputeSha256Hex(ValidKey),
                    ServiceAccountUserId = ServiceAccountId.ToString()
                }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Configuration_with_neither_key_nor_keyhash_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry> { new() { ServiceAccountUserId = ServiceAccountId.ToString() } }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Configuration_with_a_malformed_keyhash_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry>
            {
                new() { KeyHash = "not-a-valid-sha256-digest", ServiceAccountUserId = ServiceAccountId.ToString() }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Configuration_with_a_plaintext_key_duplicating_a_keyhash_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry>
            {
                new() { Key = ValidKey, ServiceAccountUserId = ServiceAccountId.ToString() },
                new() { KeyHash = McpApiKeyHasher.ComputeSha256Hex(ValidKey), ServiceAccountUserId = SecondAccountId.ToString() }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    // ---- config fail-fast (unchanged from #430) ----

    [Fact]
    public void Configuration_with_a_too_short_key_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry>
            {
                new() { Key = "short", ServiceAccountUserId = ServiceAccountId.ToString() }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Configuration_with_the_placeholder_key_fails_fast()
    {
        var options = new McpApiKeyOptions
        {
            Keys = new List<McpApiKeyEntry>
            {
                new() { Key = McpApiKeyDefaults.PlaceholderKey, ServiceAccountUserId = ServiceAccountId.ToString() }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Empty_key_list_is_valid_and_disables_the_feature()
    {
        var options = new McpApiKeyOptions();

        Should.NotThrow(() => options.Validate());
    }

    // ---- #433: rate-limited invalid-key Warning, no value leak ----

    [Fact]
    public async Task Invalid_key_logs_a_throttled_warning_that_never_contains_the_value()
    {
        const string firstBadKey = "invalid-key-one-aaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string secondBadKey = "invalid-key-two-bbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        var provider = new CapturingLoggerProvider();
        using var server = await BuildServerAsync(loggerProvider: provider);

        // Two DIFFERENT invalid keys back-to-back. The default 60s throttle window means only the first emits a
        // Warning; the second stays at Debug — an unauthenticated caller cannot flood Warning-level alerts.
        using (var client = server.CreateClient())
        {
            client.DefaultRequestHeaders.Add(HeaderName, firstBadKey);
            (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var client = server.CreateClient())
        {
            client.DefaultRequestHeaders.Add(HeaderName, secondBadKey);
            (await client.GetAsync("/mcp")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var warnings = provider.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("invalid MCP API key"))
            .ToList();

        warnings.Count.ShouldBe(1);
        warnings[0].Message.ShouldContain(HeaderName);

        provider.Entries.ShouldNotContain(e => e.Message.Contains(firstBadKey));
        provider.Entries.ShouldNotContain(e => e.Message.Contains(secondBadKey));
    }

    private static async Task<TestServer> BuildServerAsync(
        bool requireHttps = false,
        Action<McpApiKeyOptions>? customize = null,
        ILoggerProvider? loggerProvider = null,
        Guid? revokedUserId = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();

                    // Default = a policy scheme whose ForwardDefaultSelector mirrors the host's cookie selector:
                    // Bearer -> stub token scheme; /mcp + key -> McpApiKey scheme; else -> stub token (NoResult).
                    services
                        .AddAuthentication(DefaultScheme)
                        .AddPolicyScheme(DefaultScheme, displayName: null, options =>
                        {
                            options.ForwardDefaultSelector = ctx =>
                            {
                                var auth = ctx.Request.Headers.Authorization.ToString();
                                if (!string.IsNullOrWhiteSpace(auth)
                                    && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                                {
                                    return TokenScheme;
                                }

                                return ctx.RequestServices.GetRequiredService<McpApiKeyRegistry>().IsApiKeyRequest(ctx)
                                    ? McpApiKeyDefaults.AuthenticationScheme
                                    : TokenScheme;
                            };
                        })
                        .AddScheme<AuthenticationSchemeOptions, StubTokenHandler>(TokenScheme, null);

                    services.AddAuthorization();

                    services.AddVaultExtractMcpApiKey(options =>
                    {
                        options.HeaderName = HeaderName;
                        options.PathPrefix = "/mcp";
                        options.RequireHttps = requireHttps;
                        options.Keys = new List<McpApiKeyEntry>
                        {
                            new() { Key = ValidKey, ServiceAccountUserId = ServiceAccountId.ToString(), Label = "codex-test" },
                            new() { Key = SecondKey, ServiceAccountUserId = SecondAccountId.ToString(), Label = "abp-ai-test" }
                        };

                        customize?.Invoke(options);
                    });
                });
                if (loggerProvider != null)
                {
                    web.ConfigureLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Trace);
                        logging.AddProvider(loggerProvider);
                    });
                }
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.Use(async (ctx, next) =>
                    {
                        RevokeIfFlagged(ctx, revokedUserId);
                        await next();
                    });
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/mcp", (HttpContext context) => Echo(context)).RequireAuthorization();
                        endpoints.MapGet("/mcp/authresult", (HttpContext context) => AuthResultScheme(context)).RequireAuthorization();
                        endpoints.MapGet("/other", (HttpContext context) => Echo(context)).RequireAuthorization();
                    });
                });
            })
            .StartAsync();

        return host.GetTestServer();
    }

    // Stand-in for ABP's AbpDynamicClaimsMiddleware: it consumes the IAuthenticateResultFeature the auth scheme
    // produced and, for a "revoked" user, replaces the principal with an unauthenticated one (as CreateDynamicAsync
    // does when the user is gone). Runs between UseAuthentication and UseAuthorization, so RequireAuthorization
    // then 401s. It acts ONLY when the feature is present — proving the key principal carries it.
    private static void RevokeIfFlagged(HttpContext context, Guid? revokedUserId)
    {
        if (revokedUserId == null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var feature = context.Features.Get<IAuthenticateResultFeature>();
        var userId = context.User.FindFirst(AbpClaimTypes.UserId)?.Value;
        if (feature != null && userId == revokedUserId.Value.ToString())
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            feature.AuthenticateResult = AuthenticateResult.NoResult();
        }
    }

    private static string Echo(HttpContext context)
    {
        return context.User.FindFirst(AbpClaimTypes.UserId)?.Value
            ?? context.User.FindFirst("stage")?.Value
            ?? "anon";
    }

    private static string AuthResultScheme(HttpContext context)
    {
        return context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket?.AuthenticationScheme
            ?? "none";
    }

    // Bearer stub: an Authorization header authenticates with a raw principal; otherwise NoResult so the request
    // stays unauthenticated (mirroring OpenIddict validation finding no token).
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

    // Captures every log entry (level + rendered message) so a test can assert what was / was not logged.
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentBag<(LogLevel, string)> _entries;

            public CapturingLogger(ConcurrentBag<(LogLevel, string)> entries) => _entries = entries;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

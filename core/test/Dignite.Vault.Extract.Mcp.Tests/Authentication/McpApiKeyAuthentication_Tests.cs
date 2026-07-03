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
/// #428 guard for the static API-key fallback auth channel on /mcp. Uses a minimal TestServer pipeline
/// (no ABP host / DB / OpenIddict) that mirrors the host seam precisely: a default Bearer-stub scheme,
/// <c>UseVaultExtractMcpApiKey</c> BEFORE <c>UseAuthentication</c>, and the bare scheme-free
/// <c>RequireAuthorization()</c> the host uses on /mcp.
///
/// The load-bearing behaviours locked here:
///  - valid key => the request reaches the endpoint as the mapped service-account principal (proves the
///    pre-UseAuthentication principal survives AuthenticationMiddleware's NoResult and that the scheme-free
///    policy authorizes the ambient context.User);
///  - missing key => falls through to 401 (Bearer chain + #278 discovery untouched);
///  - invalid key => falls through to 401, never 403 (a 403 would suppress the #278 resource_metadata pointer);
///  - the channel is segment-scoped to /mcp and does not authenticate other paths;
///  - a valid Bearer still wins when no key is sent.
/// </summary>
public class McpApiKeyAuthentication_Tests
{
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
        // The endpoint echoes the resolved AbpClaimTypes.UserId — proves the synthetic principal survived
        // to authorization as the mapped service account.
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

        // Must be 401 (unauthenticated challenge), NOT 403 — a 403 would make the #278 discovery handler
        // skip the resource_metadata pointer and break OAuth-client discovery.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_channel_is_scoped_to_the_mcp_path()
    {
        using var server = await BuildServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        // Same valid key on a non-/mcp protected path: the middleware branch must not run, so no principal
        // is established and the request is rejected.
        var response = await client.GetAsync("/other");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_second_configured_key_authenticates_as_its_own_service_account()
    {
        // Proves the no-early-exit match loop resolves a non-first key to its own mapped account.
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
        // The middleware sets the api-key principal first, then UseAuthentication's Bearer success overwrites
        // context.User — so the Bearer principal (no UserId claim, "stage"=raw) wins, as documented.
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
        // The Bearer stub principal (not the API-key path) handled it.
        (await response.Content.ReadAsStringAsync()).ShouldBe(RawClaim);
    }

    [Fact]
    public async Task A_valid_key_over_plain_http_is_rejected_when_https_is_required()
    {
        using var server = await BuildServerAsync(requireHttps: true);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ValidKey);

        // TestServer serves plain HTTP, so the RequireHttps gate ignores the key and the request falls
        // through unauthenticated.
        var response = await client.GetAsync("/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

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

        // No throw: an OAuth-only deployment.
        Should.NotThrow(() => options.Validate());
    }

    // ---- #435: hash-at-rest (KeyHash) ----

    [Fact]
    public async Task A_keyhash_configured_key_authenticates_when_the_plaintext_is_presented()
    {
        // The entry carries only the SHA-256 digest; the client still sends the plaintext key. The runtime
        // hashes the presented value and constant-time-compares digests, so the hash-at-rest form is transparent.
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
            Keys = new List<McpApiKeyEntry>
            {
                new() { ServiceAccountUserId = ServiceAccountId.ToString() }
            }
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
                // Not 64 hex chars.
                new() { KeyHash = "not-a-valid-sha256-digest", ServiceAccountUserId = ServiceAccountId.ToString() }
            }
        };

        Should.Throw<AbpException>(() => options.Validate());
    }

    [Fact]
    public void Configuration_with_a_plaintext_key_duplicating_a_keyhash_fails_fast()
    {
        // Same secret configured twice — once as plaintext, once as its digest — must be rejected as a duplicate
        // so audit attribution / independent revocation stay meaningful.
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

        // Rate-limited to one Warning across the window despite two invalid attempts.
        warnings.Count.ShouldBe(1);
        warnings[0].Message.ShouldContain(HeaderName);

        // The secret value must never appear in any captured log entry, at any level.
        provider.Entries.ShouldNotContain(e => e.Message.Contains(firstBadKey));
        provider.Entries.ShouldNotContain(e => e.Message.Contains(secondBadKey));
    }

    private static async Task<TestServer> BuildServerAsync(
        bool requireHttps = false,
        Action<McpApiKeyOptions>? customize = null,
        ILoggerProvider? loggerProvider = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();

                    services
                        .AddAuthentication(TokenScheme)
                        .AddScheme<AuthenticationSchemeOptions, StubTokenHandler>(TokenScheme, null);

                    services.AddAuthorization();

                    services.AddVaultExtractMcpApiKey(options =>
                    {
                        options.HeaderName = HeaderName;
                        options.PathPrefix = "/mcp";
                        // TestServer requests are plain HTTP; disable the HTTPS gate except where a test
                        // explicitly exercises it.
                        options.RequireHttps = requireHttps;
                        options.Keys = new List<McpApiKeyEntry>
                        {
                            new()
                            {
                                Key = ValidKey,
                                ServiceAccountUserId = ServiceAccountId.ToString(),
                                Label = "codex-test"
                            },
                            new()
                            {
                                Key = SecondKey,
                                ServiceAccountUserId = SecondAccountId.ToString(),
                                Label = "abp-ai-test"
                            }
                        };

                        // Let a test append a KeyHash entry / tweak the throttle window without rebuilding all of this.
                        customize?.Invoke(options);
                    });
                });
                if (loggerProvider != null)
                {
                    // Capture the middleware's ILogger output (invalid-key Warning + no-value-leak assertions).
                    web.ConfigureLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Trace);
                        logging.AddProvider(loggerProvider);
                    });
                }
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseVaultExtractMcpApiKey();   // before authentication, mirroring the host
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints
                            .MapGet("/mcp", (HttpContext context) => Echo(context))
                            .RequireAuthorization();

                        endpoints
                            .MapGet("/other", (HttpContext context) => Echo(context))
                            .RequireAuthorization();
                    });
                });
            })
            .StartAsync();

        return host.GetTestServer();
    }

    private static string Echo(HttpContext context)
    {
        // API-key path stamps AbpClaimTypes.UserId; the Bearer stub stamps a "stage"=raw claim.
        return context.User.FindFirst(AbpClaimTypes.UserId)?.Value
            ?? context.User.FindFirst("stage")?.Value
            ?? "anon";
    }

    // Bearer stub: an Authorization header authenticates with a raw principal; otherwise NoResult so the
    // request stays unauthenticated (mirroring OpenIddict validation finding no token).
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Optional static API-key fallback authentication for the <c>/mcp</c> egress (#428), for MCP clients that
/// cannot run the #278 OAuth discovery flow (e.g. OpenAI Codex, ABP AI Management) but can send a static
/// request header.
///
/// <para><b>Behaviour.</b> Read the configured header; on a constant-time match, set <c>context.User</c> to
/// a synthetic authenticated principal for the mapped least-privilege service-account user. On a
/// missing/invalid key, <b>do nothing</b> (fail open, leave the principal unauthenticated — never write 401
/// or 403 here) so the request falls through to the OpenIddict Bearer chain and the #278 discovery
/// challenge stay byte-for-byte intact. Emitting a 403 here would make
/// <see cref="McpDiscoveryAuthorizationResultHandler"/> skip the <c>resource_metadata</c> pointer and break
/// discovery for OAuth clients.</para>
///
/// <para><b>Pipeline placement.</b> Runs BEFORE <c>UseAuthentication</c>. Verified (ASP.NET Core +
/// ABP 10.2.0): <c>AuthenticationMiddleware</c> only overwrites <c>context.User</c> when the default scheme
/// returns a non-null principal, so a no-Bearer request (<c>NoResult</c>) preserves the key principal; a
/// valid Bearer, when present, wins (acceptable). ABP <c>UseAbpOpenIddictValidation</c> is gated on
/// <c>!IsAuthenticated</c> and no-ops over an already-authenticated principal.</para>
///
/// <para><b>Authorization.</b> The synthetic principal carries only <c>AbpClaimTypes.UserId</c> (+ tenant /
/// label); permissions resolve from the permission store at the tools' <c>CheckPolicyAsync</c> (ABP does
/// not read permissions from claims). The endpoint keeps the bare scheme-free <c>RequireAuthorization()</c>
/// policy (the #278 invariant) which authorizes the ambient <c>context.User</c>.</para>
///
/// <para><b>Known limitation.</b> The key principal does NOT pass through <c>UseDynamicClaims</c> (it has no
/// <c>IAuthenticateResultFeature</c>), so live dynamic-claims revocation does not apply — revoke by removing
/// the key from config (or removing the service account's grant, which the permission cache picks up). A
/// future upgrade to a real <c>AuthenticationHandler</c>/scheme would restore enrichment parity; see #428.</para>
/// </summary>
public sealed class McpApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpApiKeyAuthenticationMiddleware> _logger;
    private readonly string _headerName;
    private readonly bool _requireHttps;
    private readonly long _invalidKeyWarningWindowMs;
    private readonly IReadOnlyList<CompiledKey> _keys;

    // Monotonic (boot-relative, not wall-clock) throttle gate for the invalid-key Warning (#433). One shared
    // instance backs all requests (convention middleware is instantiated once), so this is a global gate.
    private long _lastInvalidKeyWarningTicks;

    public McpApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptions<McpApiKeyOptions> options,
        ILogger<McpApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var value = options.Value;
        _headerName = value.HeaderName;
        _requireHttps = value.RequireHttps;
        _invalidKeyWarningWindowMs = Math.Max(0, value.InvalidKeyWarningWindowSeconds) * 1000L;
        // Seed the gate so the first present-but-invalid key logs a Warning immediately.
        _lastInvalidKeyWarningTicks = Environment.TickCount64 - _invalidKeyWarningWindowMs - 1;

        // Precompute the 32-byte SHA-256 digest of every configured key once — from the plaintext Key or the
        // pre-computed KeyHash (#435), whichever was configured. Comparing fixed-length digests (not the raw
        // keys) removes the length side-channel and lets FixedTimeEquals run over a constant-size buffer.
        // Options.Validate() already fail-fast-checked the hash shape at startup, so EffectiveDigest won't throw.
        _keys = value.Keys
            .Select(k => new CompiledKey(McpApiKeyHasher.EffectiveDigest(k), k))
            .ToList();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Exactly one header instance is expected. An absent header (Count == 0) is the normal OAuth path;
        // a duplicated header (Count > 1, e.g. a reverse proxy that appends instead of replaces) is
        // ambiguous. In both cases do nothing and fall through to the Bearer chain (fail open) rather than
        // matching a comma-joined StringValues that could never equal a real key.
        var values = context.Request.Headers[_headerName];
        if (values.Count == 1)
        {
            var presented = values[0];
            if (!string.IsNullOrEmpty(presented) && !RejectInsecure(context))
            {
                var matched = Match(presented);
                if (matched != null)
                {
                    context.User = McpApiKeyPrincipalFactory.Create(matched);
                    _logger.LogDebug(
                        "MCP request authenticated via API key (label: {Label}).",
                        string.IsNullOrWhiteSpace(matched.Label) ? "<unlabeled>" : matched.Label);
                }
                else
                {
                    LogInvalidKey(context);
                }
            }
        }

        await _next(context);
    }

    private bool RejectInsecure(HttpContext context)
    {
        if (!_requireHttps || context.Request.IsHttps)
        {
            return false;
        }

        // The key is a long-lived bearer-equivalent secret; refuse it over clear text and fall through to
        // the Bearer chain. Never log the value. Disable via Mcp:ApiKey:RequireHttps only for a deliberate
        // plain-HTTP deployment (e.g. local testing).
        _logger.LogDebug(
            "An MCP API key was presented over a non-HTTPS request; ignoring it (Mcp:ApiKey:RequireHttps=true).");
        return true;
    }

    private McpApiKeyEntry? Match(string presented)
    {
        var presentedHash = McpApiKeyHasher.ComputeDigest(presented);

        // Compare against every configured key with no early-exit, so neither the match position nor the
        // key count is timing-observable; SHA-256 digests are a fixed 32 bytes for FixedTimeEquals.
        McpApiKeyEntry? matched = null;
        foreach (var candidate in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, candidate.Hash))
            {
                matched = candidate.Entry;
            }
        }

        return matched;
    }

    // Present-but-invalid key: an attack signal (unlike a simply-absent key). Emit a Warning with the source IP
    // + header name (NEVER the value) so it surfaces at default log levels, throttled by ShouldWarnInvalidKey so
    // an unauthenticated caller cannot flood Warning-level logs/alerts; between windows it stays Debug. The /mcp
    // rate limiter (#433) caps how many such requests can reach here at all.
    private void LogInvalidKey(HttpContext context)
    {
        if (ShouldWarnInvalidKey())
        {
            _logger.LogWarning(
                "An invalid MCP API key was presented in header '{Header}' from {RemoteIp}; falling through to Bearer authentication.",
                _headerName,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }
        else
        {
            _logger.LogDebug(
                "An invalid MCP API key was presented in header '{Header}'; falling through to Bearer authentication.",
                _headerName);
        }
    }

    // True at most once per configured window: the window elapsed AND this thread won the compare-exchange.
    // Concurrent losers fall back to Debug. Environment.TickCount64 is monotonic (not wall-clock), so it is
    // unaffected by clock changes and needs no IClock.
    private bool ShouldWarnInvalidKey()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastInvalidKeyWarningTicks);
        if (now - last < _invalidKeyWarningWindowMs)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastInvalidKeyWarningTicks, now, last) == last;
    }

    private sealed record CompiledKey(byte[] Hash, McpApiKeyEntry Entry);
}

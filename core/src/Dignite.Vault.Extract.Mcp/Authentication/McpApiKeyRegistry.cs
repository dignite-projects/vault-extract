using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Singleton state + matching logic for the static API-key channel (#428), shared by the
/// <see cref="McpApiKeyAuthenticationHandler"/> (per-request) and the host's forwarding selector. #431 moved
/// this out of the old path-scoped middleware: an <c>AuthenticationHandler</c> is created per request, so the
/// precomputed key digests and the invalid-key log throttle must live in a singleton, not in handler instance
/// state. Behaviour is identical to the #430/#435/#433 middleware — SHA-256 digests (from plaintext <c>Key</c>
/// or pre-computed <c>KeyHash</c>), constant-time no-early-exit compare, and a rate-limited invalid-key Warning.
/// </summary>
public sealed class McpApiKeyRegistry
{
    private readonly ILogger<McpApiKeyRegistry> _logger;
    private readonly PathString _pathPrefix;
    private readonly long _invalidKeyWarningWindowMs;
    private readonly IReadOnlyList<CompiledKey> _keys;

    // Monotonic (boot-relative, not wall-clock) global throttle gate for the invalid-key Warning (#433).
    private long _lastInvalidKeyWarningTicks;

    public McpApiKeyRegistry(
        IOptions<McpApiKeyOptions> options,
        ILogger<McpApiKeyRegistry> logger)
    {
        _logger = logger;

        var value = options.Value;
        HeaderName = value.HeaderName;
        RequireHttps = value.RequireHttps;
        _pathPrefix = new PathString(value.PathPrefix);
        _invalidKeyWarningWindowMs = Math.Max(0, value.InvalidKeyWarningWindowSeconds) * 1000L;
        _lastInvalidKeyWarningTicks = Environment.TickCount64 - _invalidKeyWarningWindowMs - 1;

        // Precompute the 32-byte SHA-256 digest of every configured key once (#435). Options.Validate() already
        // fail-fast-checked the shape at startup, so EffectiveDigest won't throw.
        _keys = value.Keys
            .Select(k => new CompiledKey(McpApiKeyHasher.EffectiveDigest(k), k))
            .ToList();
    }

    /// <summary>The configured header carrying the key.</summary>
    public string HeaderName { get; }

    /// <summary>When true, a key presented over plain HTTP is ignored.</summary>
    public bool RequireHttps { get; }

    /// <summary>True when at least one key is configured (the channel is enabled).</summary>
    public bool IsEnabled => _keys.Count > 0;

    /// <summary>
    /// Whether the request should be routed to the API-key scheme by the host's <c>ForwardDefaultSelector</c>:
    /// the channel is enabled, the path is under the configured <c>/mcp</c> prefix, and exactly one key header is
    /// present. The actual (constant-time) key match happens later in the handler; this is only the routing gate.
    /// </summary>
    public bool IsApiKeyRequest(HttpContext context)
    {
        return IsEnabled
            && context.Request.Path.StartsWithSegments(_pathPrefix)
            && context.Request.Headers[HeaderName].Count == 1;
    }

    /// <summary>
    /// Constant-time match of a presented key against every configured key with no early-exit, so neither the
    /// match position nor the key count is timing-observable. Returns the matched entry, or null.
    /// </summary>
    public McpApiKeyEntry? Match(string presented)
    {
        var presentedHash = McpApiKeyHasher.ComputeDigest(presented);

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

    /// <summary>
    /// Present-but-invalid key: an attack signal. Emit a Warning with the source IP + header name (NEVER the
    /// value), throttled by a monotonic global gate so an unauthenticated caller cannot flood Warning-level
    /// logs/alerts; between windows it stays Debug. The /mcp rate limiter (#433) caps how many such requests can
    /// reach here at all.
    /// </summary>
    public void LogInvalidKey(HttpContext context)
    {
        if (ShouldWarnInvalidKey())
        {
            _logger.LogWarning(
                "An invalid MCP API key was presented in header '{Header}' from {RemoteIp}; falling through to Bearer authentication.",
                HeaderName,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }
        else
        {
            _logger.LogDebug(
                "An invalid MCP API key was presented in header '{Header}'; falling through to Bearer authentication.",
                HeaderName);
        }
    }

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

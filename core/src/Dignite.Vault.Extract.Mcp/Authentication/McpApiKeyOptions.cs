using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Options for the optional static API-key fallback auth channel on <c>/mcp</c> (#428). An empty
/// <see cref="Keys"/> list (the shipped default) means the feature is <b>disabled</b> — an OAuth-only
/// deployment — and the channel adds nothing to the request pipeline.
/// </summary>
public class McpApiKeyOptions
{
    /// <summary>Request header carrying the key. Default <see cref="McpApiKeyDefaults.DefaultHeaderName"/>.</summary>
    public string HeaderName { get; set; } = McpApiKeyDefaults.DefaultHeaderName;

    /// <summary>
    /// Endpoint path prefix the channel is scoped to. Default <see cref="McpApiKeyDefaults.DefaultPathPrefix"/>.
    /// MUST match the host's <c>MapMcp</c> path — if the host remaps the MCP endpoint, set this to match,
    /// otherwise the channel silently stops matching (the middleware no-ops and key auth breaks).
    /// </summary>
    public string PathPrefix { get; set; } = McpApiKeyDefaults.DefaultPathPrefix;

    /// <summary>
    /// When <c>true</c> (default), a key presented over a non-HTTPS request is ignored (the request falls
    /// through to Bearer) — the key is a long-lived bearer-equivalent secret and must not travel in clear
    /// text. Behind a TLS-terminating reverse proxy this relies on the host's forwarded-headers handling so
    /// <c>Request.IsHttps</c> reflects the original scheme. Set <c>false</c> only for a deliberately
    /// plain-HTTP deployment (e.g. local testing).
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Minimum seconds between <c>Warning</c>-level "present-but-invalid key" security events (#433). A
    /// present-but-invalid key is an attack signal (unlike a simply-absent one), so it is logged at Warning with
    /// the source IP + header name (never the value); this gate throttles it so an unauthenticated caller cannot
    /// flood Warning-level logs/alerts, dropping to Debug between windows. The <c>/mcp</c> rate limiter caps the
    /// request volume that can reach the middleware in the first place. Default 60s; <c>0</c> = warn every time.
    /// </summary>
    public int InvalidKeyWarningWindowSeconds { get; set; } = 60;

    /// <summary>
    /// When <c>true</c> (default <c>false</c>), the host's optional service-account seed (#434) enforces
    /// least-privilege on every configured key's <see cref="McpApiKeyEntry.ServiceAccountUserId"/>: it applies the
    /// minimal <c>VaultExtract.Documents</c> grant and fails startup if the account is missing, holds any other
    /// VaultExtract permission, or has any role. Opt-in so OAuth-only deployments and hosts that manage the
    /// accounts by hand are untouched. Seeding never creates users.
    /// </summary>
    public bool SeedServiceAccounts { get; set; } = false;

    /// <summary>Configured keys. Empty = feature disabled.</summary>
    public List<McpApiKeyEntry> Keys { get; set; } = new();

    /// <summary>
    /// Fail-fast shape validation, mirroring <c>ConfigureAI</c>'s placeholder/empty guards. A no-op when no
    /// keys are configured (the feature is simply off). When keys ARE present, every entry must be a real,
    /// sufficiently-long, non-placeholder secret mapped to a parseable user id (and tenant id if given).
    /// It does NOT verify the user exists or check its grants (there is no DB at config time) — that is
    /// enforced fail-closed at call time by the tools' <c>CheckPolicyAsync</c>.
    /// </summary>
    public void Validate()
    {
        if (Keys is null || Keys.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HeaderName))
        {
            throw new AbpException("Mcp:ApiKey:HeaderName must not be empty when API keys are configured.");
        }

        if (string.IsNullOrWhiteSpace(PathPrefix) || !PathPrefix.StartsWith('/'))
        {
            throw new AbpException(
                "Mcp:ApiKey:PathPrefix must be a non-empty path starting with '/' and must match the host's " +
                "MapMcp path (default \"/mcp\").");
        }

        for (var i = 0; i < Keys.Count; i++)
        {
            var entry = Keys[i];
            var where = $"Mcp:ApiKey:Keys[{i}]";

            ValidateSecret(entry, where);

            if (!Guid.TryParse(entry.ServiceAccountUserId, out var userId) || userId == Guid.Empty)
            {
                throw new AbpException(
                    $"{where}.ServiceAccountUserId must be the Guid of a provisioned ABP service-account user " +
                    "granted only VaultExtract.Documents (least privilege, no roles).");
            }

            if (!string.IsNullOrWhiteSpace(entry.TenantId) && !Guid.TryParse(entry.TenantId, out _))
            {
                throw new AbpException($"{where}.TenantId, when set, must be a Guid.");
            }
        }

        // Dedup on the effective digest (#435), not the raw Key, so a plaintext key and the pre-computed hash of
        // the same key are recognised as the same credential; each key must stay distinct so audit attribution
        // and independent revocation remain meaningful.
        var duplicate = Keys.GroupBy(McpApiKeyHasher.EffectiveDigestHex).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new AbpException(
                "Mcp:ApiKey:Keys contains duplicate keys (same secret configured twice, possibly one as Key and " +
                "one as KeyHash); each key must be distinct so audit attribution and independent revocation stay meaningful.");
        }
    }

    // A key is configured EITHER as plaintext Key OR as its SHA-256 KeyHash (#435), never both and never neither.
    // Plaintext mirrors ConfigureAI's placeholder/length fail-fast; the hash form only needs to be a well-formed
    // 64-char hex digest (its entropy was fixed when it was generated from the >=32-char key).
    private static void ValidateSecret(McpApiKeyEntry entry, string where)
    {
        var hasKey = !string.IsNullOrWhiteSpace(entry.Key);
        var hasHash = !string.IsNullOrWhiteSpace(entry.KeyHash);

        if (hasKey && hasHash)
        {
            throw new AbpException(
                $"{where} sets both Key and KeyHash; configure exactly one (they are interchangeable — the runtime " +
                "compares SHA-256 digests). Prefer KeyHash so a config/secret-store leak does not expose usable keys.");
        }

        if (!hasKey && !hasHash)
        {
            throw new AbpException(
                $"{where} has neither Key nor KeyHash. Supply a CSPRNG-generated secret (>= {McpApiKeyDefaults.MinKeyLength} chars) " +
                "as Key, or its lowercase hex SHA-256 as KeyHash, via environment variables or user-secrets — never commit it. " +
                "Leave Mcp:ApiKey:Keys empty to disable the API-key channel (OAuth-only).");
        }

        if (hasKey)
        {
            if (entry.Key == McpApiKeyDefaults.PlaceholderKey
                || entry.Key.Length < McpApiKeyDefaults.MinKeyLength)
            {
                throw new AbpException(
                    $"{where}.Key is the placeholder or shorter than {McpApiKeyDefaults.MinKeyLength} characters. " +
                    "Supply a CSPRNG-generated secret (>= 32 chars) via environment variables or user-secrets — never commit it.");
            }

            return;
        }

        if (entry.KeyHash!.Length != McpApiKeyHasher.Sha256HexLength || !IsHex(entry.KeyHash))
        {
            throw new AbpException(
                $"{where}.KeyHash must be a {McpApiKeyHasher.Sha256HexLength}-character hex SHA-256 digest of the key " +
                "(compute it with the documented one-liner). Leave it empty and set Key to configure the plaintext key instead.");
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

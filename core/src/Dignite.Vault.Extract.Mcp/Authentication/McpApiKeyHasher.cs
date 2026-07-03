using System;
using System.Security.Cryptography;
using System.Text;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// SHA-256 helpers shared by the API-key channel (#435). The runtime compares fixed-length SHA-256 digests
/// (not raw keys) so a key may be configured either as plaintext (<see cref="McpApiKeyEntry.Key"/>) or as its
/// pre-computed digest (<see cref="McpApiKeyEntry.KeyHash"/>, hash-at-rest). Both collapse to the same 32-byte
/// digest here, so the match loop is identical regardless of how the key was configured.
/// </summary>
public static class McpApiKeyHasher
{
    /// <summary>Length in characters of a hex-encoded SHA-256 digest (32 bytes * 2).</summary>
    public const int Sha256HexLength = 64;

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="value"/> as a 32-byte digest.</summary>
    public static byte[] ComputeDigest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Lowercase hex SHA-256 of <paramref name="value"/> — the exact form an operator configures as
    /// <see cref="McpApiKeyEntry.KeyHash"/>. Equivalent to <c>printf '%s' "&lt;key&gt;" | sha256sum</c> (bash) or
    /// <c>[BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($k)))</c>
    /// (PowerShell, minus the dashes, lowercased).
    /// </summary>
    public static string ComputeSha256Hex(string value)
    {
        return Convert.ToHexStringLower(ComputeDigest(value));
    }

    /// <summary>
    /// Decode a 64-char hex SHA-256 digest to its 32 raw bytes. Callers must validate the length/charset first
    /// (<see cref="McpApiKeyOptions.Validate"/> does, fail-fast at startup); <see cref="Convert.FromHexString(string)"/>
    /// throws on a malformed value.
    /// </summary>
    public static byte[] DecodeHexDigest(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// The effective lowercase-hex digest of an entry, whichever form it was configured in. Used for
    /// duplicate detection so a plaintext key and the hash of the same key collide.
    /// </summary>
    public static string EffectiveDigestHex(McpApiKeyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.IsNullOrWhiteSpace(entry.KeyHash)
            ? ComputeSha256Hex(entry.Key)
            : entry.KeyHash.ToLowerInvariant();
    }

    /// <summary>
    /// The effective 32-byte digest of an entry — from the pre-computed hash when present, else hashed from the
    /// plaintext key. Feeds the constant-time compare loop.
    /// </summary>
    public static byte[] EffectiveDigest(McpApiKeyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.IsNullOrWhiteSpace(entry.KeyHash)
            ? ComputeDigest(entry.Key)
            : DecodeHexDigest(entry.KeyHash);
    }
}

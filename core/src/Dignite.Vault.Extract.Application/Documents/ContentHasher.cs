using System;
using System.Security.Cryptography;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Content fingerprint utility (#210 review): SHA-256 to lowercase hex, the <b>canonical form</b> of
/// Extract content hashes.
/// Shared by upload deduplication (<c>FileOrigin.ContentHash</c>) and native payload archive manifests
/// (<c>NativePayloadManifest.Sha256</c>), keeping the hash algorithm and casing convention centralized
/// so they do not silently drift across call sites.
/// </summary>
public static class ContentHasher
{
    /// <summary>Computes SHA-256 for byte content and returns a lowercase hex string.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// One configured API key and the ABP identity it authenticates as (#428). This is host-owned deployment
/// config — the key → service-account mapping is a deployment decision; the Mcp egress module only provides
/// the matching + principal-construction mechanism. Each key SHOULD map to a dedicated, least-privilege
/// service-account user granted <b>only</b> <c>VaultExtractPermissions.Documents.Default</c>
/// (<c>"VaultExtract.Documents"</c>), with no roles, so an API-key caller can never exceed an OAuth user.
/// </summary>
public class McpApiKeyEntry
{
    /// <summary>
    /// The shared secret in plaintext. Supplied via environment variables / user-secrets, <b>never committed</b>;
    /// at least <see cref="McpApiKeyDefaults.MinKeyLength"/> characters, CSPRNG-generated. Configure <b>either</b>
    /// this <b>or</b> <see cref="KeyHash"/> for a given entry, not both — the runtime compares SHA-256 digests, so
    /// the two forms are interchangeable and setting both is ambiguous (rejected fail-fast).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Hash-at-rest alternative to <see cref="Key"/> (#435): the lowercase hex SHA-256 digest
    /// (<see cref="McpApiKeyHasher.Sha256HexLength"/> hex chars) of the shared secret. Prefer this so a leak of
    /// host config / the secret store does not hand over usable keys — the digest is not reversible to the key.
    /// Compute it with <see cref="McpApiKeyHasher.ComputeSha256Hex"/> or the documented shell one-liner. The
    /// runtime compares <c>SHA256(presented)</c> against this digest via <c>FixedTimeEquals</c>, identical to the
    /// plaintext path. Mutually exclusive with <see cref="Key"/>.
    /// </summary>
    public string? KeyHash { get; set; }

    /// <summary>
    /// The <c>Guid</c> of a real, provisioned ABP <c>IdentityUser</c> this key authenticates as; stamped as
    /// <c>AbpClaimTypes.UserId</c>. Permissions resolve from the permission store by this id at the tools'
    /// <c>CheckPolicyAsync</c> — ABP does not read permissions from principal claims.
    /// </summary>
    public string ServiceAccountUserId { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant <c>Guid</c> (stamped as <c>AbpClaimTypes.TenantId</c>). Null/empty = host space.
    /// <b>Only takes effect when the host runs multi-tenant</b> (i.e. <c>UseMultiTenancy</c> is in the
    /// pipeline). While <c>VaultExtractHostModule.IsMultiTenant</c> is <c>false</c> the claim is inert and all
    /// access resolves to the host space. When multi-tenancy is enabled this MUST be set so the ambient
    /// <c>IMultiTenant</c> filter scopes LLM-triggered queries to the right layer.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Human label for audit attribution / rotation (e.g. <c>"codex-prod"</c>). Never the key value. Logged
    /// on each successful authentication; it is NOT stamped onto the principal as a user name (that would
    /// mislead audit correlation against the real Identity user).
    /// </summary>
    public string? Label { get; set; }
}

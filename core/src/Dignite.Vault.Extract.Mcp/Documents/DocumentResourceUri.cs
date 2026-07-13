using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Single source for MCP document resource URIs. Resource templates for the read path and rows returned
/// by search tools share the same scheme, preventing hand-written <c>vault-extract://documents/...</c> values
/// from drifting across locations and breaking read-after-search.
/// </summary>
public static class DocumentResourceUri
{
    private const string Segment = "documents";
    private const string Prefix = VaultExtractMcpConsts.UriScheme + Segment + "/";

    /// <summary>Resource URI template. Used by <c>[McpServerResource(UriTemplate = ...)]</c> and must be a compile-time constant.</summary>
    public const string Template = Prefix + "{id}";

    /// <summary>Explicit-tenant resource URI template. The tenant is carried in the URI so a later resources/read keeps the selected scope.</summary>
    public const string TenantTemplate = VaultExtractMcpConsts.TenantUriRoot + "{tenantId}/" + Segment + "/{id}";

    public static string Format(Guid documentId) => Prefix + documentId;

    public static string Format(Guid documentId, Guid? tenantId) =>
        tenantId.HasValue
            ? VaultExtractMcpConsts.TenantUriRoot + tenantId.Value + "/" + Segment + "/" + documentId
            : Format(documentId);
}

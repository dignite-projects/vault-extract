using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Single source for MCP document type resource URIs. Resource templates for the read path and dynamic
/// resources/list enumeration share the same scheme, preventing hand-written
/// <c>vault-extract://document-types/...</c> values from drifting across locations. Symmetric with
/// <see cref="DocumentResourceUri"/> for document resources: documents are addressed by id, are
/// unlimited in number, are not enumerated, and are discovered through search tools; document types are
/// addressed by code, limited in number, and discovered through resources/list enumeration.
/// </summary>
public static class DocumentTypeResourceUri
{
    private const string Segment = "document-types";
    private const string Prefix = VaultExtractMcpConsts.UriScheme + Segment + "/";

    /// <summary>Resource URI template. Used by <c>[McpServerResource(UriTemplate = ...)]</c> and must be a compile-time constant.</summary>
    public const string Template = Prefix + "{code}";

    /// <summary>Explicit-tenant resource URI template.</summary>
    public const string TenantTemplate = VaultExtractMcpConsts.TenantUriRoot + "{tenantId}/" + Segment + "/{code}";

    public static string Format(string typeCode) => Prefix + typeCode;

    public static string Format(string typeCode, Guid? tenantId) =>
        tenantId.HasValue
            ? VaultExtractMcpConsts.TenantUriRoot + tenantId.Value + "/" + Segment + "/" + typeCode
            : Format(typeCode);
}

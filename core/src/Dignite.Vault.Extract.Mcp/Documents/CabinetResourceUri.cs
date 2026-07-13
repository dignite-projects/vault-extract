using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Single source for MCP cabinet resource URIs. Cabinet resources follow the existing per-item
/// document/document-type convention; bounded collection discovery is provided by resources/list
/// and the list_cabinets tool.
/// </summary>
public static class CabinetResourceUri
{
    private const string Segment = "cabinets";
    private const string Prefix = VaultExtractMcpConsts.UriScheme + Segment + "/";

    /// <summary>Resource URI template used by the MCP resource scanner.</summary>
    public const string Template = Prefix + "{id}";

    /// <summary>Explicit-tenant resource URI template.</summary>
    public const string TenantTemplate = VaultExtractMcpConsts.TenantUriRoot + "{tenantId}/" + Segment + "/{id}";

    public static string Format(Guid cabinetId) => Prefix + cabinetId;

    public static string Format(Guid cabinetId, Guid? tenantId) =>
        tenantId.HasValue
            ? VaultExtractMcpConsts.TenantUriRoot + tenantId.Value + "/" + Segment + "/" + cabinetId
            : Format(cabinetId);
}

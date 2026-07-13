using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Single source for MCP cabinet resource URIs. Cabinet resources follow the existing per-item
/// document/document-type convention; bounded collection discovery is provided by resources/list
/// and the list_cabinets tool.
/// </summary>
public static class CabinetResourceUri
{
    private const string Prefix = "vault-extract://cabinets/";
    private const string TenantPrefix = "vault-extract://tenants/";

    /// <summary>Resource URI template used by the MCP resource scanner.</summary>
    public const string Template = Prefix + "{id}";

    /// <summary>Explicit-tenant resource URI template.</summary>
    public const string TenantTemplate = TenantPrefix + "{tenantId}/cabinets/{id}";

    public static string Format(Guid cabinetId) => Prefix + cabinetId;

    public static string Format(Guid cabinetId, Guid? tenantId) =>
        tenantId.HasValue
            ? TenantPrefix + tenantId.Value + "/cabinets/" + cabinetId
            : Format(cabinetId);
}

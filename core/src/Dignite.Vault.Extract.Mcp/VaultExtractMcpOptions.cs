using Volo.Abp.Collections;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Extension seam of the MCP outbound adapter for downstream modules (e.g. a commercial edition layered
/// on top of the open-source channel, #475). <see cref="ResourceListContributors"/> is the ordered category
/// list behind <c>resources/list</c>: <c>VaultExtractMcpModule</c> registers the built-in document-type
/// and cabinet categories, and a downstream module appends its own in
/// <c>Configure&lt;VaultExtractMcpOptions&gt;</c>. A contributor added here must also be DI-registered
/// (e.g. via <c>ITransientDependency</c>) — the catalog resolves entries from the request scope by type.
/// Contributors added here list <c>vault-extract://</c> resources only - the shared MCP server rejects a URI
/// outside this module's scheme; a downstream product with a scheme of its own registers its own MCP module.
/// Tools need no options entry — a downstream module adds tool classes to this namespace additively via
/// <c>context.Services.AddAbpMcpModule("vault_extract", mcp => mcp.AddTools&lt;TTools&gt;())</c>, naming them
/// <c>vault_extract_…</c>.
/// <see cref="AllowExplicitTenantScope"/> is a separate, unrelated deployment-level gate: it does not
/// register anything, it only decides whether the explicit-<c>tenantId</c> tool parameters / resource-uri
/// segments may be used at all (#524).
/// </summary>
public class VaultExtractMcpOptions
{
    public ITypeList<IMcpResourceListContributor> ResourceListContributors { get; } =
        new TypeList<IMcpResourceListContributor>();

    /// <summary>
    /// Deployment-level opt-in for the explicit <c>tenantId</c> tool parameter / mandatory
    /// <c>{tenantId}</c> resource-uri segment (#519). Defaults to <c>false</c>: an authenticated caller
    /// may otherwise request any tenant id and, absent this gate, the ambient tenant switch would run
    /// before the target application service's own authorization check evaluates it (#524). Enabling this
    /// alone does not authorize anything — it only unlocks the capability; a deployment must also supply
    /// its own <see cref="Documents.IMcpTenantAccessValidator"/> to decide which callers may target which
    /// tenants, because Extract itself has no concept of cross-tenant membership.
    /// </summary>
    public bool AllowExplicitTenantScope { get; set; } = false;
}

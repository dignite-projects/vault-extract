using Volo.Abp.Collections;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Extension seam of the MCP outbound adapter for downstream modules (e.g. a commercial edition layered
/// on top of the open-source channel, #475). <see cref="ResourceListContributors"/> is the ordered category
/// list behind <c>resources/list</c>: <c>VaultExtractMcpModule</c> registers the built-in document-type
/// and cabinet categories, and a downstream module appends its own in
/// <c>Configure&lt;VaultExtractMcpOptions&gt;</c>. A contributor added here must also be DI-registered
/// (e.g. via <c>ITransientDependency</c>) — the catalog resolves entries from the request scope by type.
/// Tools need no options entry — a downstream module adds
/// tool classes additively via <c>context.Services.AddMcpServer().WithTools&lt;TTools&gt;()</c>.
/// </summary>
public class VaultExtractMcpOptions
{
    public ITypeList<IMcpResourceListContributor> ResourceListContributors { get; } =
        new TypeList<IMcpResourceListContributor>();
}

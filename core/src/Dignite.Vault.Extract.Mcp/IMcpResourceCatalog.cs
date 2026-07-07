using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Composes the MCP <c>resources/list</c> response from the categories registered in
/// <see cref="VaultExtractMcpOptions.ResourceListContributors"/>.
/// </summary>
public interface IMcpResourceCatalog
{
    Task<ListResourcesResult> ListVisibleAsync(CancellationToken cancellationToken = default);
}

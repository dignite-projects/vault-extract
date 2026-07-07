using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// One independently authorized resource category of the MCP <c>resources/list</c> composition.
/// Categories are registered by type in <see cref="VaultExtractMcpOptions.ResourceListContributors"/> and
/// resolved from the request scope, so a downstream module can append its own category without replacing
/// the built-in catalog. Contract: return <c>null</c> when the calling principal lacks this category's
/// read permission — categories never cross-gate each other, and <see cref="McpResourceCatalog"/> fails
/// closed only when every contributor returns <c>null</c>. A granted category returns a bounded, possibly
/// empty list (llm-call-anti-patterns counterexample B point 3: unbounded enumeration is forbidden), and
/// any delegated AppService still repeats its own fail-closed authorization assertion.
/// </summary>
public interface IMcpResourceListContributor
{
    Task<IList<Resource>?> ListAsync(CancellationToken cancellationToken = default);
}

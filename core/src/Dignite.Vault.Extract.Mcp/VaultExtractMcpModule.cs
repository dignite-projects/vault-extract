using Dignite.Vault.Extract.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/// <summary>
/// Extract MCP outbound adapter, parallel to the REST <c>HttpApi</c> outbound surface.
/// Exposes channel documents as MCP resources + search tools for Claude Desktop / Cursor / any MCP
/// client. MCP SDK dependencies stay in this project and do not leak into Application; endpoint
/// mapping (<c>MapMcp</c>) remains host-only. Authentication reuses the host's existing OpenIddict
/// Bearer setup through RequireAuthorization on the endpoint; the optional #278 OAuth Protected
/// Resource Metadata discovery flow is exported as the reusable
/// <see cref="Authentication.McpDiscoveryServiceCollectionExtensions.AddVaultExtractMcpDiscovery"/> so any
/// host enables it with one call instead of re-authoring the handler (#422). Subscription + lifecycle notifications
/// are future incremental work (#197). The outbound surface depends only on
/// <c>Application.Contracts</c>, symmetric with REST: all read paths go through AppService interfaces
/// and do not reach into Domain (#222).
/// </summary>
[DependsOn(
    typeof(VaultExtractApplicationContractsModule))]
public class VaultExtractMcpModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Streamable HTTP transport. Capabilities declare only plain resources/tools, with no
        // subscribe / listChanged support, honestly advertising pull-only behavior so clients do not
        // wait for push notifications (#197 will add that later).
        context.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithResources<DocumentResources>()
            .WithResources<DocumentTypeResources>()
            .WithResources<CabinetResources>()
            .WithTools<DocumentSearchTool>()
            .WithTools<DocumentTypeTools>()
            .WithTools<CabinetTools>()
            .WithTools<DocumentTools>()
            // resources/list dynamically enumerates document types and cabinets visible to the current
            // principal. Documents themselves are not enumerated because their count is unbounded; they
            // are discovered through search_documents.
            .WithListResourcesHandler(async (ctx, ct) =>
            {
                return await McpResourceCatalog.ListVisibleAsync(ctx.Services!);
            });
    }
}

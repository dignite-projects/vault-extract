using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/// <summary>
/// Extract MCP outbound adapter, parallel to the REST <c>HttpApi</c> outbound surface.
/// Exposes channel documents as MCP resources + search tools for Claude Desktop / Cursor / any MCP
/// client. MCP SDK dependencies stay in this project and do not leak into Application; endpoint
/// mapping (<c>MapMcp</c>) remains host-only. Authentication reuses the host's existing OpenIddict
/// Bearer setup through RequireAuthorization on the endpoint. Subscription + lifecycle notifications
/// are future incremental work (#197). The outbound surface depends only on
/// <c>Application.Contracts</c>, symmetric with REST: all read paths go through AppService interfaces
/// and do not reach into Domain (#222).
/// </summary>
[DependsOn(
    typeof(ExtractApplicationContractsModule))]
public class ExtractMcpModule : AbpModule
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
            .WithTools<DocumentSearchTool>()
            .WithTools<DocumentTypeTools>()
            .WithTools<DocumentTools>()
            // resources/list dynamically enumerates document types visible to the current principal.
            // AI uses it to discover documentTypeCode values, then reads each
            // vault-extract://document-types/{code} resource to get the field schema. Documents themselves
            // are not enumerated because their count is unbounded; they are discovered through the
            // search tool. The read path is still automatically routed by DocumentTypeResources'
            // UriTemplate. list and read responsibilities stay separate: this handler only fills
            // resources/list, and template reads are unaffected.
            .WithListResourcesHandler(async (ctx, ct) =>
            {
                // Delegate to DocumentTypeResources.ListVisibleAsync. The projection centralizes
                // fail-closed authorization assertions (OR assertion inside the AppService method
                // body; MCP dispatch does not pass through HTTP [Authorize], but in-process AppService
                // calls still execute normally), ambient tenant isolation (two-layer independent
                // single-layer model), and hard result truncation after TypeCode ordering
                // (ExtractMcpConsts.MaxDocumentTypeResults).
                var documentTypeAppService = ctx.Services!.GetRequiredService<IDocumentTypeAppService>();
                return await DocumentTypeResources.ListVisibleAsync(documentTypeAppService);
            });
    }
}

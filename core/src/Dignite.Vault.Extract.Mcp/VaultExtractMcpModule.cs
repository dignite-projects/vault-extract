using Dignite.Abp.AspNetCore.Mcp;
using Dignite.Vault.Extract.Mcp;
using Dignite.Vault.Extract.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/// <summary>
/// Extract MCP outbound adapter, parallel to the REST <c>HttpApi</c> outbound surface.
/// Exposes channel documents as MCP resources + search tools for Claude Desktop / Cursor / any MCP
/// client. MCP SDK dependencies stay in this project and do not leak into Application.
/// <para>
/// <b>This module contributes to the application's MCP server; it does not host one.</b> The server -
/// Streamable HTTP transport, the <c>/mcp</c> endpoint, <c>tools/list</c> permission filtering, the
/// structured error envelope, the RFC 9728 discovery challenge - is
/// <see cref="AbpAspNetCoreMcpModule"/> (Dignite.Abp.AspNetCore.Mcp), shared with every other module that
/// contributes tools to the same host. Everything here lives in the <c>vault_extract</c> namespace: tool
/// names start with <c>vault_extract_</c> and resources use <c>vault-extract://</c>, which the server
/// enforces at startup. Authentication reuses the host's existing OpenIddict Bearer setup through the
/// endpoint's default policy, so ABP's dynamic claims reach every MCP request.
/// </para>
/// <para>
/// Subscription + lifecycle notifications are future incremental work (#197). The outbound surface
/// depends only on <c>Application.Contracts</c>, symmetric with REST: all read paths go through AppService
/// interfaces and do not reach into Domain (#222). It is additively extensible by downstream modules (e.g.
/// a commercial edition, #475): extra tools in this namespace by calling
/// <c>context.Services.AddAbpMcpModule("vault_extract", mcp => mcp.AddTools&lt;TTools&gt;())</c> again from
/// their own module; extra <c>vault-extract://</c> resources/list categories via
/// <see cref="VaultExtractMcpOptions.ResourceListContributors"/>. A downstream product with resources under
/// a scheme of its own registers its own MCP module (its own name, its own scheme) instead.
/// Built-in tools and resource templates use the ambient tenant by default, and also accept an explicit tenant
/// scope while preserving ABP's enabled IMultiTenant filter and application-service authorization checks.
/// </para>
/// </summary>
[DependsOn(
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpAspNetCoreMcpModule))]
public class VaultExtractMcpModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // resources/list composition is contributor-based: each entry is one independently authorized,
        // bounded category, and downstream modules append their own categories through the same options.
        Configure<VaultExtractMcpOptions>(options =>
        {
            options.ResourceListContributors.Add<DocumentTypeMcpResourceListContributor>();
            options.ResourceListContributors.Add<CabinetMcpResourceListContributor>();
        });

        // Single explicit registration, not conventional auto-registration (#524): see the reasoning on
        // IMcpTenantAccessValidator. TryAdd so a downstream module that already registered its own
        // implementation before this module's ConfigureServices runs is not clobbered; the expected
        // override path is still context.Services.Replace(...) in a module that depends on this one.
        context.Services.TryAddTransient<IMcpTenantAccessValidator, DenyAllMcpTenantAccessValidator>();

        // Capabilities declare only plain resources/tools, with no subscribe / listChanged support,
        // honestly advertising pull-only behavior so clients do not wait for push notifications (#197).
        context.Services.AddAbpMcpModule(VaultExtractMcpConsts.ModuleName, mcp => mcp
            .AddResources<DocumentResources>()
            .AddResources<DocumentTypeResources>()
            .AddResources<CabinetResources>()
            .AddTools<DocumentSearchTool>()
            .AddTools<DocumentTypeTools>()
            .AddTools<CabinetTools>()
            .AddTools<DocumentTools>()
            // resources/list dynamically enumerates the registered categories (document types and
            // cabinets by default) visible to the current principal. Documents themselves are not
            // enumerated because their count is unbounded; they are discovered through
            // vault_extract_search_documents. Contributed through the server's shared listing rather than
            // WithListResourcesHandler, a single slot the next module to set it would overwrite.
            .AddResourceListContributor<VaultExtractMcpResourceListContributor>());
    }
}

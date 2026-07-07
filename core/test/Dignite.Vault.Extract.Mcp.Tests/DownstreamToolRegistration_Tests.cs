using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Stands in for a commercial-edition tool set (e.g. an authorized cross-tenant search). Description is
/// a compile-time constant per llm-call-anti-patterns.
/// </summary>
[McpServerToolType]
public sealed class FakeDownstreamTools
{
    [McpServerTool(Name = "search_tenant_documents")]
    [Description("Test-only downstream tool standing in for a commercial cross-tenant search.")]
    public static string Search(string query) => query;
}

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class UpstreamMcpToolRegistrationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
        context.Services.AddMcpServer().WithTools<DocumentSearchTool>();
    }
}

[DependsOn(typeof(UpstreamMcpToolRegistrationTestModule))]
public class DownstreamMcpToolRegistrationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The documented downstream seam: a second module calls AddMcpServer().WithTools again to add
        // its own tool classes next to the open-source ones.
        context.Services.AddMcpServer().WithTools<FakeDownstreamTools>();
    }
}

/// <summary>
/// Guards the tool extension seam for downstream modules: calling
/// <c>AddMcpServer().WithTools&lt;TTools&gt;()</c> from a second ABP module is additive — the built-in
/// tool set is neither replaced nor duplicated. Runs under Autofac like production (base class uses
/// UseAutofac).
/// </summary>
public class DownstreamToolRegistration_Tests : VaultExtractTestBase<DownstreamMcpToolRegistrationTestModule>
{
    [Fact]
    public void Second_module_adds_tools_additively_without_disturbing_built_ins()
    {
        var toolNames = ServiceProvider.GetServices<McpServerTool>()
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        toolNames.ShouldContain("search_documents");
        toolNames.ShouldContain("search_tenant_documents");
        toolNames.Count(n => n == "search_documents").ShouldBe(1);
        toolNames.Count(n => n == "search_tenant_documents").ShouldBe(1);
    }
}

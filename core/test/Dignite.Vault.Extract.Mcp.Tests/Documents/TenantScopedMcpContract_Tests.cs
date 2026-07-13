using System;
using System.Linq;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class TenantScopedMcpContractTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
        context.Services.AddSingleton(Substitute.For<ICabinetReadAppService>());

        context.Services
            .AddMcpServer()
            .WithResources<DocumentResources>()
            .WithResources<DocumentTypeResources>()
            .WithResources<CabinetResources>();
    }
}

/// <summary>
/// Contract guards for the MCP SDK discovery surface. Direct C# calls cannot prove that the LLM sees
/// a tool parameter or that the URI templates are registered for resources/templates/list.
/// </summary>
public class TenantScopedMcpContract_Tests : VaultExtractTestBase<TenantScopedMcpContractTestModule>
{
    [Theory]
    [InlineData(typeof(DocumentSearchTool), nameof(DocumentSearchTool.SearchAsync))]
    [InlineData(typeof(DocumentTools), nameof(DocumentTools.GetAsync))]
    [InlineData(typeof(DocumentTypeTools), nameof(DocumentTypeTools.ListAsync))]
    [InlineData(typeof(CabinetTools), nameof(CabinetTools.ListAsync))]
    public void Tool_schema_exposes_tenant_id(Type toolType, string methodName)
    {
        var method = toolType.GetMethod(methodName)!;
        var tool = McpServerTool.Create(
            method,
            target: null,
            new McpServerToolCreateOptions { Services = ServiceProvider });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        properties.TryGetProperty("tenantId", out _).ShouldBeTrue();
        properties.TryGetProperty("serviceProvider", out _).ShouldBeFalse();
    }

    [Fact]
    public void Resource_template_discovery_includes_ambient_and_explicit_tenant_forms()
    {
        var templates = ServiceProvider.GetServices<McpServerResource>()
            .Where(resource => resource.IsTemplated)
            .Select(resource => resource.ProtocolResourceTemplate!.UriTemplate)
            .ToList();

        templates.ShouldContain(DocumentResourceUri.Template);
        templates.ShouldContain(DocumentResourceUri.TenantTemplate);
        templates.ShouldContain(DocumentTypeResourceUri.Template);
        templates.ShouldContain(DocumentTypeResourceUri.TenantTemplate);
        templates.ShouldContain(CabinetResourceUri.Template);
        templates.ShouldContain(CabinetResourceUri.TenantTemplate);
    }
}

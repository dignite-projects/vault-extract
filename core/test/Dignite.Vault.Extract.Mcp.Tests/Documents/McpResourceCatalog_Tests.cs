using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

public sealed class McpCatalogGrantAuthorizationService : IAbpAuthorizationService
{
    public HashSet<string> Granted { get; set; } = new();

    public ClaimsPrincipal CurrentPrincipal => null!;
    public IServiceProvider ServiceProvider => null!;

    public Task<AuthorizationResult> AuthorizeAsync(
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(object? resource, string policyName)
        => Task.FromResult(Evaluate(policyName));

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        string policyName)
        => Task.FromResult(Evaluate(policyName));

    private AuthorizationResult Evaluate(string policyName)
        => Granted.Contains(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed();

    private AuthorizationResult Evaluate(IEnumerable<IAuthorizationRequirement> requirements)
    {
        foreach (var requirement in requirements)
        {
            if (requirement is PermissionRequirement permission &&
                Granted.Contains(permission.PermissionName))
            {
                return AuthorizationResult.Success();
            }
        }

        return AuthorizationResult.Failed();
    }
}

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class McpResourceCatalogTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var authorizationService = new McpCatalogGrantAuthorizationService();
        context.Services.AddSingleton(authorizationService);
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(authorizationService);
        context.Services.AddSingleton<IAbpAuthorizationService>(authorizationService);

        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<ICabinetReadAppService>());
    }
}

public class McpResourceCatalog_Tests : VaultExtractTestBase<McpResourceCatalogTestModule>
{
    private readonly McpCatalogGrantAuthorizationService _authorization;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly ICabinetReadAppService _cabinetReadAppService;

    public McpResourceCatalog_Tests()
    {
        _authorization = GetRequiredService<McpCatalogGrantAuthorizationService>();
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
    }

    [Fact]
    public async Task Document_type_admin_lists_types_without_cabinet_permission()
    {
        _authorization.Granted = new HashSet<string>
        {
            VaultExtractPermissions.DocumentTypes.Default
        };
        _documentTypeAppService.GetVisibleAsync().Returns(new List<DocumentTypeDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TypeCode = "contract.general",
                DisplayName = "Contract"
            }
        });

        var result = await McpResourceCatalog.ListVisibleAsync(ServiceProvider);

        result.Resources.Count.ShouldBe(1);
        result.Resources[0].Uri.ShouldBe(
            DocumentTypeResourceUri.Format("contract.general"));
        await _cabinetReadAppService.DidNotReceive().GetListAsync();
    }

    [Fact]
    public async Task Cabinet_admin_lists_cabinets_without_document_type_permission()
    {
        _authorization.Granted = new HashSet<string>
        {
            VaultExtractPermissions.Cabinets.Default
        };
        var cabinetId = Guid.NewGuid();
        _cabinetReadAppService.GetListAsync().Returns(
            new PagedResultDto<CabinetDto>(1, new List<CabinetDto>
            {
                new() { Id = cabinetId, Name = "Legal" }
            }));

        var result = await McpResourceCatalog.ListVisibleAsync(ServiceProvider);

        result.Resources.Count.ShouldBe(1);
        result.Resources[0].Uri.ShouldBe(CabinetResourceUri.Format(cabinetId));
        await _documentTypeAppService.DidNotReceive().GetVisibleAsync();
    }

    [Fact]
    public async Task Caller_without_any_resource_read_permission_is_denied()
    {
        _authorization.Granted.Clear();

        await Should.ThrowAsync<AbpAuthorizationException>(() =>
            McpResourceCatalog.ListVisibleAsync(ServiceProvider));
    }
}

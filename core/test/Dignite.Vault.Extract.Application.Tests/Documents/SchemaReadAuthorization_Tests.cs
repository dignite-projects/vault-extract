using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Test authorization service that grants policies one by one by policy name, which is also the permission name.
/// It implements <see cref="IAbpAuthorizationService"/> because ABP's <c>IsGrantedAsync</c> / <c>CheckAsync</c>
/// extensions cast <see cref="IAuthorizationService"/> to that interface.
/// All string / requirements overloads route to the same grant set and do not rely on framework-internal routing details.
/// </summary>
public sealed class GrantSetAuthorizationService : IAbpAuthorizationService
{
    public HashSet<string> Granted { get; set; } = new();

    // Test stub: the IsGrantedAsync / CheckAsync extensions only call AuthorizeAsync and do not read these members.
    public ClaimsPrincipal CurrentPrincipal => null!;
    public IServiceProvider ServiceProvider => null!;

    // IAbpAuthorizationService: the extension methods actually use these two 2-argument overloads.
    public Task<AuthorizationResult> AuthorizeAsync(object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(object? resource, string policyName)
        => Task.FromResult(Evaluate(policyName));

    // IAuthorizationService
    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        => Task.FromResult(Evaluate(policyName));

    private AuthorizationResult Evaluate(string policyName)
        => Granted.Contains(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed();

    private AuthorizationResult Evaluate(IEnumerable<IAuthorizationRequirement> requirements)
    {
        foreach (var requirement in requirements)
        {
            if (requirement is PermissionRequirement permission && Granted.Contains(permission.PermissionName))
            {
                return AuthorizationResult.Success();
            }
        }

        return AuthorizationResult.Failed();
    }
}

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class SchemaReadAuthorizationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Replace the always-allow IAuthorizationService with a controllable grant set. This is the only
        // decision source for GetVisibleAsync, the active-field GetListAsync programmatic OR gate, and the
        // trash-view CheckPolicyAsync call.
        var authorizationService = new GrantSetAuthorizationService();
        context.Services.AddSingleton(authorizationService);
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(authorizationService);
        context.Services.AddSingleton<IAbpAuthorizationService>(authorizationService);

        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
    }
}

/// <summary>
/// #223: schema reads are decoupled from schema administration. Read paths
/// (<c>GetVisibleAsync</c> / active-field <c>GetListAsync</c>) accept either <c>Documents.Default</c>
/// or the corresponding schema-admin permission; trash reads remain schema-admin only.
/// </summary>
public class SchemaReadAuthorization_Tests : VaultExtractApplicationTestBase<SchemaReadAuthorizationTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly ICabinetReadAppService _cabinetReadAppService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly GrantSetAuthorizationService _authorization;

    public SchemaReadAuthorization_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
    }

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    // ---- DocumentType reads: GetVisibleAsync ----

    [Fact]
    public async Task GetVisibleAsync_Throws_When_Neither_Documents_Nor_DocumentTypes_Granted()
    {
        Grant(/* nothing */);

        await Should.ThrowAsync<AbpAuthorizationException>(() => _documentTypeAppService.GetVisibleAsync());
    }

    [Fact]
    public async Task GetVisibleAsync_Succeeds_For_Documents_Default_Only()
    {
        // #223 fix point: document operators without DocumentTypes.Default can still read type schema
        // for filters, field columns, and classification assignment.
        Grant(VaultExtractPermissions.Documents.Default);
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType> { new(Guid.NewGuid(), null, "host.general", "General") });

        var result = await _documentTypeAppService.GetVisibleAsync();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetVisibleAsync_Succeeds_For_DocumentTypes_Default_Only()
    {
        // Schema administrators without Documents.Default can still read their management list.
        Grant(VaultExtractPermissions.DocumentTypes.Default);
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType> { new(Guid.NewGuid(), null, "host.general", "General") });

        var result = await _documentTypeAppService.GetVisibleAsync();

        result.Count.ShouldBe(1);
    }

    // ---- Cabinet reads: bounded non-HTTP read service ----

    [Fact]
    public async Task CabinetRead_Throws_When_Neither_Documents_Nor_Cabinets_Granted()
    {
        Grant(/* nothing */);

        await Should.ThrowAsync<AbpAuthorizationException>(() => _cabinetReadAppService.GetListAsync());
    }

    [Fact]
    public async Task CabinetRead_Succeeds_For_Documents_Default_Only()
    {
        Grant(VaultExtractPermissions.Documents.Default);
        _cabinetRepository.GetQueryableAsync()
            .Returns(new List<Cabinet> { new(Guid.NewGuid(), null, "Legal") }.AsQueryable());

        var result = await _cabinetReadAppService.GetListAsync();

        result.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CabinetRead_Succeeds_For_Cabinets_Default_Only()
    {
        Grant(VaultExtractPermissions.Cabinets.Default);
        _cabinetRepository.GetQueryableAsync()
            .Returns(new List<Cabinet> { new(Guid.NewGuid(), null, "Legal") }.AsQueryable());

        var result = await _cabinetReadAppService.GetListAsync();

        result.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CabinetRead_Applies_Database_Query_Cap_And_Returns_Genuine_Total()
    {
        Grant(VaultExtractPermissions.Documents.Default);
        var cabinets = Enumerable.Range(0, CabinetReadConsts.MaxResultCount + 5)
            .Select(i => new Cabinet(Guid.NewGuid(), null, $"Cabinet {i:D4}"))
            .OrderByDescending(c => c.Name)
            .AsQueryable();
        _cabinetRepository.GetQueryableAsync().Returns(cabinets);

        var result = await _cabinetReadAppService.GetListAsync();

        result.TotalCount.ShouldBe(CabinetReadConsts.MaxResultCount + 5);
        result.Items.Count.ShouldBe(CabinetReadConsts.MaxResultCount);
        result.Items[0].Name.ShouldBe("Cabinet 0000");
    }

    // ---- FieldDefinition reads: active GetListAsync ----

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Throws_When_Neither_Granted()
    {
        Grant(/* nothing */);

        await Should.ThrowAsync<AbpAuthorizationException>(() =>
            _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
            {
                DocumentTypeId = Guid.NewGuid(),
                OnlyDeleted = false
            }));
    }

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Succeeds_For_Documents_Default_Only()
    {
        // #223 fix point: document operators read field schema for dynamic field columns,
        // detail-field editing, and export-column selection.
        Grant(VaultExtractPermissions.Documents.Default);
        _fieldDefinitionRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
        {
            DocumentTypeId = Guid.NewGuid(),
            OnlyDeleted = false
        });

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Succeeds_For_FieldDefinitions_Default_Only()
    {
        Grant(VaultExtractPermissions.FieldDefinitions.Default);
        _fieldDefinitionRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
        {
            DocumentTypeId = Guid.NewGuid(),
            OnlyDeleted = false
        });

        result.ShouldNotBeNull();
    }

    // ---- FieldDefinition trash: OnlyDeleted remains schema-admin only ----

    [Fact]
    public async Task FieldDefinition_GetListAsync_Deleted_Throws_For_Documents_Default_Only()
    {
        // The trash view keeps the admin gate. Documents.Default cannot open it through the OR rule because
        // CheckPolicyAsync runs before the query.
        Grant(VaultExtractPermissions.Documents.Default);

        await Should.ThrowAsync<AbpAuthorizationException>(() =>
            _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
            {
                DocumentTypeId = Guid.NewGuid(),
                OnlyDeleted = true
            }));
    }
}

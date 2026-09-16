using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// #629 phase 1: the per-document-type <c>Upload</c> resource permission — its frozen contract strings, its
/// registration, and the way <c>GetVisibleAsync</c> reports a caller's own grants back on the DTO.
/// <para>
/// The grants are resolved through ABP's real <c>ResourcePermissionChecker</c> and its real user / role value
/// providers, reading an <see cref="InMemoryResourcePermissionStore"/> that stands in for the
/// <c>AbpResourcePermissionGrants</c> table (see <see cref="GrantSetAuthorizationService"/> for the seam). Only
/// the storage is faked, so an ABP-side change to how a claim becomes a grant lookup would red these.
/// </para>
/// </summary>
public class DocumentTypeResourcePermissions_Tests
    : VaultExtractApplicationTestBase<SchemaReadAuthorizationTestModule>
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string RoleName = "uploader";

    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionDefinitionManager _permissionDefinitionManager;
    private readonly InMemoryResourcePermissionStore _resourcePermissionStore;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly GrantSetAuthorizationService _authorization;

    public DocumentTypeResourcePermissions_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionDefinitionManager = GetRequiredService<IPermissionDefinitionManager>();
        _resourcePermissionStore = GetRequiredService<InMemoryResourcePermissionStore>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
    }

    // ---- The frozen contract ----

    /// <summary>
    /// The guard the Issue asks for: <c>Resources.Name</c> is a literal in Application.Contracts (which cannot
    /// reference Domain), but ABP derives the resource name from the runtime type of the object handed to
    /// <c>AuthorizationService</c>. Rename or move the entity and every existing grant row silently stops
    /// matching — this test is what turns that into a build-time failure instead.
    /// </summary>
    [Fact]
    public void Resource_name_equals_the_entity_type_full_name()
    {
        VaultExtractPermissions.DocumentTypes.Resources.Name.ShouldBe(typeof(DocumentType).FullName);
    }

    [Fact]
    public void Resource_permission_names_are_prefixed_with_the_resource_name()
    {
        // Not cosmetic: VaultExtractPermissions.GetAll() filters the resource family out by exactly this prefix.
        VaultExtractPermissions.DocumentTypes.Resources.Upload
            .ShouldStartWith(VaultExtractPermissions.DocumentTypes.Resources.Name + ".");
    }

    [Fact]
    public void GetAll_returns_standard_permissions_only()
    {
        var all = VaultExtractPermissions.GetAll();

        all.ShouldContain(VaultExtractPermissions.DocumentTypes.ManagePermissions);
        all.ShouldNotContain(VaultExtractPermissions.DocumentTypes.Resources.Name);
        all.ShouldNotContain(VaultExtractPermissions.DocumentTypes.Resources.Upload);
    }

    // ---- Registration ----

    [Fact]
    public async Task Definition_provider_registers_the_management_permission()
    {
        var definition = await _permissionDefinitionManager.GetOrNullAsync(
            VaultExtractPermissions.DocumentTypes.ManagePermissions);

        definition.ShouldNotBeNull();
        definition.Parent?.Name.ShouldBe(VaultExtractPermissions.DocumentTypes.Default);
    }

    [Fact]
    public async Task Definition_provider_registers_the_upload_resource_permission()
    {
        var definition = await _permissionDefinitionManager.GetResourcePermissionOrNullAsync(
            VaultExtractPermissions.DocumentTypes.Resources.Name,
            VaultExtractPermissions.DocumentTypes.Resources.Upload);

        definition.ShouldNotBeNull();
        definition.ResourceName.ShouldBe(VaultExtractPermissions.DocumentTypes.Resources.Name);

        // The gate on ABP's /api/permission-management/permissions/resource* endpoints. It is deliberately NOT
        // DocumentTypes.Update: handing out access is a different responsibility from editing the schema.
        definition.ManagementPermissionName.ShouldBe(VaultExtractPermissions.DocumentTypes.ManagePermissions);
        definition.ManagementPermissionName.ShouldNotBe(VaultExtractPermissions.DocumentTypes.Update);

        // Document types exist on the Host layer and on every tenant layer; a grant carries its own TenantId.
        definition.MultiTenancySide.ShouldBe(MultiTenancySides.Both);
    }

    [Fact]
    public async Task The_upload_resource_permission_is_not_a_standard_permission()
    {
        // A resource permission must never be grantable from the ordinary permission-management grid, or an
        // operator could hand out "upload into every type" while believing they granted one type.
        (await _permissionDefinitionManager.GetOrNullAsync(
            VaultExtractPermissions.DocumentTypes.Resources.Upload)).ShouldBeNull();
    }

    // ---- GetVisibleAsync fills ResourcePermissions ----

    [Fact]
    public async Task GetVisibleAsync_reports_the_upload_grant_for_exactly_the_granted_types_of_an_upload_only_caller()
    {
        var granted = new DocumentType(Guid.NewGuid(), null, "invoice.general", "Invoice");
        var other = new DocumentType(Guid.NewGuid(), null, "contract.general", "Contract");
        StubTypes(granted, other);

        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, UserId.ToString(), granted.Id);

        var result = await GetVisibleAsPrincipalAsync(PrincipalWithUser());

        result.Single(t => t.Id == granted.Id)
            .ResourcePermissions[VaultExtractPermissions.DocumentTypes.Resources.Upload].ShouldBeTrue();
        result.Single(t => t.Id == other.Id)
            .ResourcePermissions[VaultExtractPermissions.DocumentTypes.Resources.Upload].ShouldBeFalse();
    }

    [Fact]
    public async Task GetVisibleAsync_reports_a_role_grant_too()
    {
        var granted = new DocumentType(Guid.NewGuid(), null, "invoice.general", "Invoice");
        StubTypes(granted);

        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(RoleResourcePermissionValueProvider.ProviderName, RoleName, granted.Id);

        var result = await GetVisibleAsPrincipalAsync(PrincipalWithUserAndRole());

        result.Single().ResourcePermissions[VaultExtractPermissions.DocumentTypes.Resources.Upload].ShouldBeTrue();
    }

    [Fact]
    public async Task GetVisibleAsync_fills_the_dictionary_for_a_Documents_Default_caller_too()
    {
        // The dictionary is the UI's single source of truth, so it is populated for every caller rather than
        // only for the upload-only ones. A ConfirmClassification holder is "all types" by a different rule and
        // is deliberately NOT reflected as a resource grant here.
        var type = new DocumentType(Guid.NewGuid(), null, "invoice.general", "Invoice");
        StubTypes(type);

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);

        var result = await GetVisibleAsPrincipalAsync(PrincipalWithUser());

        var dto = result.Single();
        dto.TypeCode.ShouldBe("invoice.general");
        dto.ResourcePermissions.ShouldContainKey(VaultExtractPermissions.DocumentTypes.Resources.Upload);
        dto.ResourcePermissions[VaultExtractPermissions.DocumentTypes.Resources.Upload].ShouldBeFalse();
    }

    [Fact]
    public async Task GetVisibleAsync_does_not_leak_another_principals_grant()
    {
        // Guards the reason IResourcePermissionStore.GetGrantedResourceKeysAsync was rejected: it filters on
        // resource + permission name only, so "some grant exists" would read as "this caller may upload".
        var type = new DocumentType(Guid.NewGuid(), null, "invoice.general", "Invoice");
        StubTypes(type);

        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, Guid.NewGuid().ToString(), type.Id);

        var result = await GetVisibleAsPrincipalAsync(PrincipalWithUser());

        result.Single().ResourcePermissions[VaultExtractPermissions.DocumentTypes.Resources.Upload].ShouldBeFalse();
    }

    // ---- helpers ----

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    private void GrantResource(string providerName, string providerKey, Guid documentTypeId)
    {
        _resourcePermissionStore.Grant(
            VaultExtractPermissions.DocumentTypes.Resources.Upload,
            VaultExtractPermissions.DocumentTypes.Resources.Name,
            documentTypeId.ToString(),
            providerName,
            providerKey);
    }

    private void StubTypes(params DocumentType[] types)
    {
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(types.ToList());
    }

    private async Task<List<DocumentTypeDto>> GetVisibleAsPrincipalAsync(ClaimsPrincipal principal)
    {
        using (_principalAccessor.Change(principal))
        {
            return await _documentTypeAppService.GetVisibleAsync();
        }
    }

    // The claim shapes ABP's resource permission value providers actually read: AbpClaimTypes.UserId for "U"
    // and AbpClaimTypes.Role for "R". Nothing else about the principal matters to them.
    private static ClaimsPrincipal PrincipalWithUser()
        => new(new ClaimsIdentity(
            [new Claim(AbpClaimTypes.UserId, UserId.ToString())],
            authenticationType: "ApplicationTest"));

    private static ClaimsPrincipal PrincipalWithUserAndRole()
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, UserId.ToString()),
                new Claim(AbpClaimTypes.Role, RoleName)
            ],
            authenticationType: "ApplicationTest"));
}

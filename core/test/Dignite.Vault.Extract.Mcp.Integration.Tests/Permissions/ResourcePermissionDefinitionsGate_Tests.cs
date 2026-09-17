using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Permissions;

/// <summary>
/// #629 acceptance: <c>GET /api/permission-management/permissions/resource-definitions?resourceName=…DocumentType</c>
/// lists the <c>Upload</c> resource-permission definition for a <see cref="VaultExtractPermissions.DocumentTypes.ManagePermissions"/>
/// holder and nothing for anyone else. This lives in the MCP Integration Tests project because it is the only test
/// host in this repo that boots a REAL permission pipeline (no <c>AddAlwaysAllowAuthorization</c>) — see
/// <see cref="McpPermissionPipelineTestModule"/>. The gate under test is ABP's own <c>PermissionAppService.GetResourceDefinitionsAsync</c>
/// (only definitions whose <c>ManagementPermissionName</c> the caller is granted are returned); Dignite Vault
/// Extract's own contribution is the definition wiring — <c>Upload</c> is managed by <c>ManagePermissions</c>,
/// deliberately NOT by <c>Update</c> (see <c>VaultExtractPermissionDefinitionProvider</c>).
/// </summary>
public class ResourcePermissionDefinitionsGate_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    // Each fact grants its own fixed user id, distinct from the others, so the three facts don't interfere
    // (the DB connection is shared for the app lifetime — see McpPermissionPipelineTestModule).
    private static readonly Guid ManagePermissionsHolderId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid UpdateOnlyHolderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid UngrantedPrincipalId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private const string UserProviderName = "U";

    private readonly IPermissionAppService _permissionAppService;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public ResourcePermissionDefinitionsGate_Tests()
    {
        _permissionAppService = GetRequiredService<IPermissionAppService>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    /// <summary>
    /// #632 acceptance ("the definition test lists all four"): the dialog's checkbox set is exactly
    /// Upload / Read / Edit / Delete, all four managed by <c>ManagePermissions</c>. The exact count is asserted on
    /// purpose — a fifth definition appearing here without a decision is the failure mode worth catching.
    /// </summary>
    [Fact]
    public async Task ManagePermissions_holder_sees_all_four_definitions()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await GrantAsync(VaultExtractPermissions.DocumentTypes.ManagePermissions, ManagePermissionsHolderId);
        });

        using (_principalAccessor.Change(ServiceAccountPrincipal(ManagePermissionsHolderId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var result = await _permissionAppService.GetResourceDefinitionsAsync(
                    VaultExtractResourcePermissions.Name);

                result.Permissions.Count.ShouldBe(4);
                result.Permissions.ShouldContain(p => p.Name == VaultExtractResourcePermissions.Upload);
                result.Permissions.ShouldContain(p => p.Name == VaultExtractResourcePermissions.Read);
                result.Permissions.ShouldContain(p => p.Name == VaultExtractResourcePermissions.Edit);
                result.Permissions.ShouldContain(p => p.Name == VaultExtractResourcePermissions.Delete);
            });
        }
    }

    // This is the assertion that proves the gate is ManagePermissions, not the schema-editing permission: a
    // holder of the sibling standard permission Update (schema CRUD) must NOT see any resource definition.
    [Fact]
    public async Task Update_holder_without_ManagePermissions_sees_nothing()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await GrantAsync(VaultExtractPermissions.DocumentTypes.Update, UpdateOnlyHolderId);
        });

        using (_principalAccessor.Change(ServiceAccountPrincipal(UpdateOnlyHolderId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var result = await _permissionAppService.GetResourceDefinitionsAsync(
                    VaultExtractResourcePermissions.Name);

                result.Permissions.ShouldBeEmpty();
            });
        }
    }

    [Fact]
    public async Task Ungranted_principal_sees_nothing()
    {
        using (_principalAccessor.Change(ServiceAccountPrincipal(UngrantedPrincipalId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var result = await _permissionAppService.GetResourceDefinitionsAsync(
                    VaultExtractResourcePermissions.Name);

                result.Permissions.ShouldBeEmpty();
            });
        }
    }

    private async Task GrantAsync(string permissionName, Guid userId)
    {
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(),
                permissionName,
                UserProviderName,
                userId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    // Same claim shape as McpPermissionResolution_Tests: a bare ClaimsPrincipal carrying only AbpClaimTypes.UserId
    // plus a non-empty authentication type, so PermissionAppService's [Authorize] is satisfied and IPermissionChecker
    // resolves grants from the store by user id.
    private static ClaimsPrincipal ServiceAccountPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(AbpClaimTypes.UserId, userId.ToString()) },
            authenticationType: "IntegrationTest");
        return new ClaimsPrincipal(identity);
    }
}

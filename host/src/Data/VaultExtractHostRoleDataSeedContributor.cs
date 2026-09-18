using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Identity; // AbpIdentityResultExtensions.CheckErrors(), applied to IdentityRoleManager.CreateAsync's result below
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.Identity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.PermissionManagement;
using IdentityRole = Volo.Abp.Identity.IdentityRole; // disambiguate from Microsoft.AspNetCore.Identity.IdentityRole

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// Seeds the two operator roles (<c>DocumentManager</c> / <c>Viewer</c>) into whichever layer the
/// seed's <see cref="DataSeedContext.TenantId"/> names. <see cref="IdentityRole"/>'s <c>tenantId</c>
/// constructor parameter defaults to <c>null</c>, so a seed that ignores it always lands in the Host
/// layer — including when ABP's <c>TenantAppService.CreateAsync</c> runs this contributor under its own
/// <c>CurrentTenant.Change(tenant.Id)</c> scope. There, the tenant-filtered role lookup finds nothing,
/// creates a second Host-layer row, and the permission grants that follow get written against a role
/// name the tenant layer has no row for (#640). Scoping with <see cref="ICurrentTenant.Change"/> and
/// passing the tenant id into the <see cref="IdentityRole"/> constructor keeps each layer's pair
/// independent, matching ABP's own <c>IdentityDataSeeder</c>.
/// </summary>
public class VaultExtractHostRoleDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IIdentityRoleRepository _roleRepository;
    private readonly IdentityRoleManager _roleManager;
    private readonly IPermissionManager _permissionManager;

    public VaultExtractHostRoleDataSeedContributor(
        ICurrentTenant currentTenant,
        IGuidGenerator guidGenerator,
        IIdentityRoleRepository roleRepository,
        IdentityRoleManager roleManager,
        IPermissionManager permissionManager)
    {
        _currentTenant = currentTenant;
        _guidGenerator = guidGenerator;
        _roleRepository = roleRepository;
        _roleManager = roleManager;
        _permissionManager = permissionManager;
    }

    public virtual async Task SeedAsync(DataSeedContext context)
    {
        using (_currentTenant.Change(context?.TenantId))
        {
            await SeedRoleAsync("DocumentManager", new[]
            {
                VaultExtractPermissions.Documents.Default,
                // #632 split Documents.Default into ENTRY (Default) and READ EVERY TYPE (ReadAll). Without ReadAll a
                // role sees only the types it holds a per-type Read grant on, so both seeded roles carry it and read
                // exactly as they did before the change. SeedRoleAsync re-applies each permission idempotently, so
                // existing deployments pick it up on their next migration run; hand-made roles and MCP OAuth clients
                // need a manual grant (CHANGELOG migration note).
                VaultExtractPermissions.Documents.ReadAll,
                VaultExtractPermissions.Documents.Upload,
                VaultExtractPermissions.Documents.Export,
                // DocumentManager is an operator role that edits and reviews: ConfirmClassification is the role-level
                // "edit (and review) documents of every type", and it is also what lets the role assign any type on
                // confirm / reclassify. (Uploading into any type, untyped included, is Documents.Upload above since #645.)
                // SeedRoleAsync re-applies each listed permission idempotently, so existing deployments pick this up on their next migration run.
                VaultExtractPermissions.Documents.ConfirmClassification,
            }, context?.TenantId);

            await SeedRoleAsync("Viewer", new[]
            {
                VaultExtractPermissions.Documents.Default,
                // A Viewer that can enter the area but read nothing would be an empty list (#632).
                VaultExtractPermissions.Documents.ReadAll,
            }, context?.TenantId);
        }
    }

    protected virtual async Task SeedRoleAsync(string roleName, string[] permissions, Guid? tenantId)
    {
        var role = await _roleRepository.FindByNormalizedNameAsync(roleName.ToUpperInvariant());
        if (role == null)
        {
            (await _roleManager.CreateAsync(new IdentityRole(_guidGenerator.Create(), roleName, tenantId))).CheckErrors();
            role = await _roleRepository.FindByNormalizedNameAsync(roleName.ToUpperInvariant());
        }

        foreach (var permission in permissions)
        {
            await _permissionManager.SetForRoleAsync(roleName, permission, true);
        }
    }
}

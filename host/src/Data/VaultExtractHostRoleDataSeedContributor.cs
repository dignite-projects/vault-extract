using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;

namespace Dignite.Vault.Extract.Host.Data;

public class VaultExtractHostRoleDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IIdentityRoleRepository _roleRepository;
    private readonly IdentityRoleManager _roleManager;
    private readonly IPermissionManager _permissionManager;

    public VaultExtractHostRoleDataSeedContributor(
        IIdentityRoleRepository roleRepository,
        IdentityRoleManager roleManager,
        IPermissionManager permissionManager)
    {
        _roleRepository = roleRepository;
        _roleManager = roleManager;
        _permissionManager = permissionManager;
    }

    public virtual async Task SeedAsync(DataSeedContext context)
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
            // #629 made Upload alone inert (untyped upload requires ConfirmClassification, typed upload requires a per-type resource grant), so the seeded operator role carries ConfirmClassification;
            // SeedRoleAsync re-applies each listed permission idempotently, so existing deployments pick this up on their next migration run.
            VaultExtractPermissions.Documents.ConfirmClassification,
        });

        await SeedRoleAsync("Viewer", new[]
        {
            VaultExtractPermissions.Documents.Default,
            // A Viewer that can enter the area but read nothing would be an empty list (#632).
            VaultExtractPermissions.Documents.ReadAll,
        });
    }

    private async Task SeedRoleAsync(string roleName, string[] permissions)
    {
        var role = await _roleRepository.FindByNormalizedNameAsync(roleName.ToUpperInvariant());
        if (role == null)
        {
            await _roleManager.CreateAsync(new IdentityRole(System.Guid.NewGuid(), roleName));
            role = await _roleRepository.FindByNormalizedNameAsync(roleName.ToUpperInvariant());
        }

        foreach (var permission in permissions)
        {
            await _permissionManager.SetForRoleAsync(roleName, permission, true);
        }
    }
}

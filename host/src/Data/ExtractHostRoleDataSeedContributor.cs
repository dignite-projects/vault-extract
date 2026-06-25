using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;

namespace Dignite.Vault.Extract.Host.Data;

public class ExtractHostRoleDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IIdentityRoleRepository _roleRepository;
    private readonly IdentityRoleManager _roleManager;
    private readonly IPermissionManager _permissionManager;

    public ExtractHostRoleDataSeedContributor(
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
            ExtractPermissions.Documents.Default,
            ExtractPermissions.Documents.Upload,
            ExtractPermissions.Documents.Export,
        });

        await SeedRoleAsync("Viewer", new[]
        {
            ExtractPermissions.Documents.Default,
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Mcp.Authentication;
using Dignite.Vault.Extract.Permissions;
using Microsoft.Extensions.Options;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.PermissionManagement;

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// #434: optional, opt-in provisioning guard for the MCP API-key service accounts. When
/// <c>Mcp:ApiKey:SeedServiceAccounts</c> is <c>true</c>, each configured key's service-account user is pinned to
/// least privilege: the minimal <c>VaultExtract.Documents</c> grant is applied at the user level, and startup
/// fails fail-closed if the account is missing, holds any other VaultExtract permission, or has any role
/// (a shared role could later widen it silently beyond an OAuth user). It never creates users — the operator
/// provisions the account (admin UI) and copies its Guid into config. Disabled by default so OAuth-only
/// deployments and hand-managed accounts are untouched. The least-privilege decision is the pure, unit-tested
/// <see cref="McpServiceAccountLeastPrivilege"/> guard in the Mcp module; this contributor only gathers the
/// account's real ABP state and applies the grant.
/// </summary>
public class McpServiceAccountSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly McpApiKeyOptions _options;
    private readonly IIdentityUserRepository _userRepository;
    private readonly IdentityUserManager _userManager;
    private readonly IPermissionManager _permissionManager;
    private readonly ICurrentTenant _currentTenant;

    public McpServiceAccountSeedContributor(
        IOptions<McpApiKeyOptions> options,
        IIdentityUserRepository userRepository,
        IdentityUserManager userManager,
        IPermissionManager permissionManager,
        ICurrentTenant currentTenant)
    {
        _options = options.Value;
        _userRepository = userRepository;
        _userManager = userManager;
        _permissionManager = permissionManager;
        _currentTenant = currentTenant;
    }

    public virtual async Task SeedAsync(DataSeedContext context)
    {
        if (!_options.SeedServiceAccounts || _options.Keys.Count == 0)
        {
            return;
        }

        // Dedup: several keys (e.g. during rotation) may map to the same account; process each once.
        var accounts = _options.Keys
            .GroupBy(k => (k.ServiceAccountUserId, k.TenantId))
            .Select(g => new {
                UserId = Guid.Parse(g.Key.ServiceAccountUserId),
                TenantId = string.IsNullOrWhiteSpace(g.Key.TenantId) ? (Guid?)null : Guid.Parse(g.Key.TenantId!),
                Label = g.Select(k => k.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                RawUserId = g.Key.ServiceAccountUserId
            });

        foreach (var account in accounts)
        {
            using (_currentTenant.Change(account.TenantId))
            {
                await EnforceLeastPrivilegeAsync(account.UserId, account.RawUserId, account.Label);
            }
        }
    }

    protected virtual async Task EnforceLeastPrivilegeAsync(Guid userId, string rawUserId, string? label)
    {
        var user = await _userRepository.FindAsync(userId);
        var userExists = user != null;

        IReadOnlyCollection<string> roleNames = userExists
            ? (await _userManager.GetRolesAsync(user!)).ToList()
            : Array.Empty<string>();

        // Apply the minimal grant (idempotent) before the guard reads it back, so a freshly-provisioned account
        // converges to least privilege. If the user is missing, skip granting — the guard fails on non-existence.
        if (userExists)
        {
            await _permissionManager.SetForUserAsync(userId, McpServiceAccountLeastPrivilege.AllowedPermission, isGranted: true);
        }

        var grantedVaultExtractPermissions = userExists
            ? await GetGrantedVaultExtractPermissionsAsync(userId)
            : Array.Empty<string>();

        McpServiceAccountLeastPrivilege.Guard(rawUserId, label, userExists, grantedVaultExtractPermissions, roleNames);
    }

    // ABP's user-scoped permission value provider name (UserPermissionValueProvider.ProviderName == "U").
    private const string UserProviderName = "U";

    // The VaultExtract permission names granted to this user directly at the user level (provider "U").
    protected virtual async Task<IReadOnlyCollection<string>> GetGrantedVaultExtractPermissionsAsync(Guid userId)
    {
        var granted = new List<string>();
        foreach (var permission in VaultExtractPermissions.GetAll())
        {
            var result = await _permissionManager.GetAsync(permission, UserProviderName, userId.ToString());
            if (result.IsGranted)
            {
                granted.Add(permission);
            }
        }

        return granted;
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// In-memory <see cref="IResourcePermissionStore"/> standing in for the EF-backed
/// <c>AbpResourcePermissionGrants</c> table (#629). It holds exactly what a grant row holds —
/// <c>(name, resourceName, resourceKey, providerName, providerKey)</c> — so ABP's real
/// <c>UserResourcePermissionValueProvider</c> ("U", provider key = user id) and
/// <c>RoleResourcePermissionValueProvider</c> ("R", provider key = role name) run unmodified above it.
/// <para>
/// The bulk overload returns <see cref="PermissionGrantResult.Undefined"/> for a miss, matching the real
/// <c>Volo.Abp.PermissionManagement.ResourcePermissionStore</c> and <b>not</b> ABP's own test fake, which
/// returns <c>Prohibited</c>. The difference is load-bearing: <c>ResourcePermissionChecker</c> stops consulting
/// further providers once a result is Prohibited, so a Prohibited miss from the user provider would hide every
/// role grant and the role acceptance case would fail for a reason that does not exist in production.
/// </para>
/// </summary>
public sealed class InMemoryResourcePermissionStore : IResourcePermissionStore
{
    private readonly HashSet<GrantRow> _grants = new();

    /// <summary>Number of store lookups, so a test can assert the permission layer was never consulted.</summary>
    public int LookupCount { get; private set; }

    public void Grant(string name, string resourceName, string resourceKey, string providerName, string providerKey)
    {
        _grants.Add(new GrantRow(name, resourceName, resourceKey, providerName, providerKey));
    }

    /// <summary>Zeroes <see cref="LookupCount"/> without touching the grants, to time a single assertion.</summary>
    public void ResetLookupCount() => LookupCount = 0;

    public Task<bool> IsGrantedAsync(string name, string resourceName, string resourceKey, string providerName, string providerKey)
    {
        LookupCount++;
        return Task.FromResult(_grants.Contains(new GrantRow(name, resourceName, resourceKey, providerName, providerKey)));
    }

    public Task<MultiplePermissionGrantResult> IsGrantedAsync(string[] names, string resourceName, string resourceKey, string providerName, string providerKey)
    {
        LookupCount++;

        var result = new MultiplePermissionGrantResult();
        foreach (var name in names)
        {
            result.Result[name] = _grants.Contains(new GrantRow(name, resourceName, resourceKey, providerName, providerKey))
                ? PermissionGrantResult.Granted
                : PermissionGrantResult.Undefined;
        }

        return Task.FromResult(result);
    }

    // Nothing on the #629 path calls these. GetGrantedResourceKeysAsync in particular is the API the Issue
    // rejected for filling DocumentTypeDto.ResourcePermissions, because it filters on resource + permission name
    // only and so is not per-user; throwing here means a future switch to it reds instead of silently widening
    // what an upload-only caller is told it may declare.
    public Task<MultiplePermissionGrantResult> GetPermissionsAsync(string resourceName, string resourceKey)
        => throw new NotSupportedException("Not used by the #629 path.");

    public Task<string[]> GetGrantedPermissionsAsync(string resourceName, string resourceKey)
        => throw new NotSupportedException("Not used by the #629 path.");

    public Task<string[]> GetGrantedResourceKeysAsync(string resourceName, string name)
        => throw new NotSupportedException(
            "Deliberately unreachable: GetGrantedResourceKeysAsync is not per-user (see #629 decision 3).");

    private readonly record struct GrantRow(
        string Name,
        string ResourceName,
        string ResourceKey,
        string ProviderName,
        string ProviderKey);
}

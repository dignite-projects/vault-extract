using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.Security.Claims;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Test authorization service that grants <b>standard</b> policies one by one by policy name, which is also the
/// permission name. It implements <see cref="IAbpAuthorizationService"/> because ABP's <c>IsGrantedAsync</c> /
/// <c>CheckAsync</c> extensions cast <see cref="IAuthorizationService"/> to that interface.
/// <para>
/// <b>Resource permissions are deliberately NOT faked</b> (#629). Whenever the call carries a resource — the
/// shape <c>AuthorizationService.IsGrantedAsync(documentType, Resources.Upload)</c> produces — this delegates to
/// the container's real <see cref="IResourcePermissionChecker"/>, so ABP's own
/// <c>UserResourcePermissionValueProvider</c> / <c>RoleResourcePermissionValueProvider</c> resolve the grant from
/// the ambient principal's claims against whatever <see cref="IResourcePermissionStore"/> the test registered
/// (<see cref="InMemoryResourcePermissionStore"/>). Stubbing that half would have made the hand-written
/// module-permission-OR-resource-grant rule in <c>DocumentAppService.UploadAsync</c> assert itself.
/// </para>
/// <para>
/// The delegation mirrors ABP's <c>KeyedObjectResourcePermissionRequirementHandler</c> exactly: resource name
/// from the runtime type, resource key from <see cref="IKeyedObject.GetObjectKey"/>. A resource-bearing call
/// whose policy is a standard permission (or a name the checker does not know as a resource permission) falls
/// back to the grant set, so the two mechanisms never shadow each other.
/// </para>
/// </summary>
public sealed class GrantSetAuthorizationService : IAbpAuthorizationService
{
    private readonly IServiceProvider _serviceProvider;

    public GrantSetAuthorizationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public HashSet<string> Granted { get; set; } = new();

    // Read on every call: the parameterless AuthorizeAsync/IsGrantedAsync extensions route through it. It has to
    // be the ambient principal, not null, or the resource value providers would see no user/role claims.
    public ClaimsPrincipal CurrentPrincipal
        => _serviceProvider.GetRequiredService<ICurrentPrincipalAccessor>().Principal;

    public IServiceProvider ServiceProvider => _serviceProvider;

    // IAbpAuthorizationService: the extension methods actually use these two 2-argument overloads.
    public Task<AuthorizationResult> AuthorizeAsync(object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => AuthorizeAsync(CurrentPrincipal, resource, requirements);

    public Task<AuthorizationResult> AuthorizeAsync(object? resource, string policyName)
        => AuthorizeAsync(CurrentPrincipal, resource, policyName);

    // IAuthorizationService
    public async Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
    {
        foreach (var requirement in requirements)
        {
            switch (requirement)
            {
                case PermissionRequirement permission when Granted.Contains(permission.PermissionName):
                    return AuthorizationResult.Success();

                case ResourcePermissionRequirement resourcePermission
                    when resource is IKeyedObject keyed
                         && await IsResourceGrantedAsync(user, keyed, resourcePermission.PermissionName):
                    return AuthorizationResult.Success();
            }
        }

        return AuthorizationResult.Failed();
    }

    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        if (resource is IKeyedObject keyed && await IsResourceGrantedAsync(user, keyed, policyName))
        {
            return AuthorizationResult.Success();
        }

        return Granted.Contains(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed();
    }

    private async Task<bool> IsResourceGrantedAsync(ClaimsPrincipal? user, IKeyedObject resource, string permissionName)
    {
        var resourceKey = resource.GetObjectKey();
        if (resourceKey.IsNullOrEmpty())
        {
            return false;
        }

        // Resolved per call rather than injected: the checker is transient and this stub is a singleton.
        return await _serviceProvider
            .GetRequiredService<IResourcePermissionChecker>()
            .IsGrantedAsync(user, permissionName, resource.GetType().FullName!, resourceKey!);
    }
}

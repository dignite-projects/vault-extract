using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Every permission answer this request has already paid for</b> (#635 decision 4) — the per-type grants and
/// the standard permission names alike, each asked once and then remembered for the rest of the request.
/// <para>
/// Per type, it asks ABP's <b>multi-name</b> <see cref="IResourcePermissionChecker"/> overload — the same one
/// ABP's own <c>ResourcePermissionPopulator</c> uses — so all four grants on that type cost one round trip.
/// </para>
/// <para>
/// <b>Lazy per type, not per layer.</b> Asking about one document asks about one type;
/// <see cref="TypesWithAsync"/> is the only thing that sweeps the layer, and it reuses whatever per-type answers
/// are already in hand. That is what makes <b>reaching</b> one's own document cost zero grant checks, a whole
/// detail page (which projects six rights, one of which an owner cannot answer) cost exactly one, and neither of
/// them read the type table at all — the sweep exists for the list, the recycle bin, the export and the duplicate
/// panel, which genuinely need to know the whole set.
/// </para>
/// <para>
/// <b>No parallel resolution.</b> The sweep is a sequential loop on purpose: a cache miss can reach the EF-backed
/// permission store, which shares this unit of work's <c>DbContext</c>, and a <c>Task.WhenAll</c> over those
/// would use it concurrently.
/// </para>
/// <para>
/// <see cref="IScopedDependency"/>, so the lifetime <i>is</i> the request and there is no invalidation to get
/// wrong — except one: ABP's ambient tenant and principal can both be changed <b>inside</b> a scope
/// (<c>ICurrentTenant.Change</c> on the MCP explicit-tenant path, <c>ICurrentPrincipalAccessor.Change</c> in a
/// background job or a test). Every answer therefore records the identity it was resolved for, and the memo
/// resets when the ambient identity differs. Without that, a switched principal would inherit the first one's
/// grants.
/// </para>
/// </summary>
public class DocumentTypeGrantMap : IScopedDependency
{
    /// <summary>
    /// The four names asked per type, in one call. Asking for all four regardless of which rule prompted the load
    /// is the point: the second question about that type is free, and it is what lets the per-row rights (six
    /// answers over three grants) cost nothing beyond the first.
    /// </summary>
    private static readonly string[] AllGrants =
    [
        VaultExtractResourcePermissions.Upload,
        VaultExtractResourcePermissions.Read,
        VaultExtractResourcePermissions.Edit,
        VaultExtractResourcePermissions.Delete
    ];

    private readonly IResourcePermissionChecker _resourcePermissionChecker;
    private readonly IAuthorizationService _authorizationService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;

    private readonly Dictionary<Guid, HashSet<string>> _grantsByType = new();
    private readonly Dictionary<string, bool> _standardPermissions = new(StringComparer.Ordinal);
    private bool _layerSwept;
    private (Guid? TenantId, Guid? UserId, string? ClientId)? _identity;

    public DocumentTypeGrantMap(
        IResourcePermissionChecker resourcePermissionChecker,
        IAuthorizationService authorizationService,
        IDocumentTypeRepository documentTypeRepository,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser)
    {
        _resourcePermissionChecker = resourcePermissionChecker;
        _authorizationService = authorizationService;
        _documentTypeRepository = documentTypeRepository;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Whether the caller holds one <b>standard</b> permission — entry, or a rule's module-wide name. Memoised
    /// for the same reason the grants are: resolving a document's six rights would otherwise ask
    /// <c>Documents.Default</c> six times and <c>ConfirmClassification</c> twice, and a list page multiplies that
    /// by its distinct subjects.
    /// </summary>
    public virtual async Task<bool> IsPermissionGrantedAsync(string permissionName)
    {
        ResetIfIdentityChanged();

        if (_standardPermissions.TryGetValue(permissionName, out var granted))
        {
            return granted;
        }

        granted = await _authorizationService.IsGrantedAsync(permissionName);
        _standardPermissions[permissionName] = granted;
        return granted;
    }

    /// <summary>
    /// Does the caller hold <paramref name="grant"/> on this one type? Resolves <b>that type only</b> on a miss.
    /// </summary>
    public virtual async Task<bool> HasAsync(Guid documentTypeId, string grant)
    {
        var names = await EnsureTypeLoadedAsync(documentTypeId);
        return names.Contains(grant);
    }

    /// <summary>
    /// Every type of the layer the caller holds <paramref name="grant"/> on — the set a
    /// <see cref="DocumentAccessScope"/> is built from. Empty is a real answer, not a missing one.
    /// <para>
    /// This is the one shape that reads the type table, and it does so once per request however many grants are
    /// asked about afterwards.
    /// </para>
    /// </summary>
    public virtual async Task<IReadOnlySet<Guid>> TypesWithAsync(string grant)
    {
        await EnsureLayerSweptAsync();

        var result = new HashSet<Guid>();
        foreach (var (typeId, names) in _grantsByType)
        {
            if (names.Contains(grant))
            {
                result.Add(typeId);
            }
        }

        return result;
    }

    /// <summary>One type's four answers, from the memo or from one multi-name check.</summary>
    protected virtual async Task<HashSet<string>> EnsureTypeLoadedAsync(Guid documentTypeId)
    {
        ResetIfIdentityChanged();

        if (_grantsByType.TryGetValue(documentTypeId, out var cached))
        {
            return cached;
        }

        var names = await CheckAllGrantsAsync(documentTypeId);
        _grantsByType[documentTypeId] = names;
        return names;
    }

    /// <summary>
    /// Sweeps the layer once: the types not already resolved get one multi-name check each, sequentially.
    /// </summary>
    protected virtual async Task EnsureLayerSweptAsync()
    {
        ResetIfIdentityChanged();

        if (_layerSwept)
        {
            return;
        }

        foreach (var type in await GetLayerTypesAsync())
        {
            if (!_grantsByType.ContainsKey(type.Id))
            {
                _grantsByType[type.Id] = await CheckAllGrantsAsync(type.Id);
            }
        }

        _layerSwept = true;
    }

    /// <summary>
    /// ABP's multi-name overload: one store round trip (and one distributed-cache read) answers all four grants
    /// for this type. <c>MultiplePermissionGrantResult</c> reports <c>Undefined</c> for a miss, which is not
    /// <c>Granted</c> — fail-closed without an explicit else.
    /// </summary>
    protected virtual async Task<HashSet<string>> CheckAllGrantsAsync(Guid documentTypeId)
    {
        var result = await _resourcePermissionChecker.IsGrantedAsync(
            AllGrants,
            VaultExtractResourcePermissions.Name,
            documentTypeId.ToString());

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, grantResult) in result.Result)
        {
            if (grantResult == PermissionGrantResult.Granted)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Drops every memoised answer when the ambient tenant or principal is not the one they were resolved for.
    /// <para>
    /// A scoped service normally needs no invalidation, but both of these <b>are</b> changed inside a scope in
    /// this codebase: <c>McpTenantScope</c> changes the tenant for an explicit-tenant MCP call, background jobs
    /// and tests change the principal. Answers memoised for one identity must not be read by another, and
    /// "reset on difference" is the only rule that cannot be forgotten at a new call site.
    /// </para>
    /// </summary>
    protected virtual void ResetIfIdentityChanged()
    {
        var current = (_currentTenant.Id, _currentUser.Id, _currentUser.FindClaimValue(AbpClaimTypesClientId));
        if (_identity is { } identity && identity == current)
        {
            return;
        }

        ResetMemo();
        _identity = current;
    }

    /// <summary>
    /// Forgets every answer. Separated from <see cref="ResetIfIdentityChanged"/> so a subclass can add a reason
    /// of its own to reset — the test host changes what a principal is granted <b>inside</b> one scope, which no
    /// request ever does.
    /// </summary>
    protected virtual void ResetMemo()
    {
        _grantsByType.Clear();
        _standardPermissions.Clear();
        _layerSwept = false;
    }

    /// <summary>
    /// <c>Volo.Abp.Security.Claims.AbpClaimTypes.ClientId</c>'s value. Referenced as a literal so this project
    /// does not take a dependency on the claim-type constants for one string; the frozen-string discipline does
    /// not apply (it is ABP's own claim name, not a persisted contract of ours), but a rename upstream would show
    /// up as a machine identity never resetting, so it is named here rather than inlined at the call site.
    /// </summary>
    private const string AbpClaimTypesClientId = "client_id";

    /// <summary>
    /// The layer's own document types — the set <see cref="TypesWithAsync"/> enumerates.
    /// <para>
    /// Soft delete is traversed on purpose, the same way <c>DocumentAppService.ResolveReferenceMapsAsync</c>
    /// traverses it: a document classified to a since-archived type is still in the list of a
    /// <c>Documents.ReadAll</c> holder, so a caller holding an explicit grant on that type must see it too —
    /// otherwise the narrow caller and the module-wide caller disagree about which rows exist. It also makes the
    /// sweep independent of whether the CALLER happens to be inside <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c>
    /// (the recycle-bin branch of the list is), which would otherwise silently widen or narrow it by call site.
    /// </para>
    /// <para>
    /// This can only ever ADD a type the caller was explicitly granted; the <c>IMultiTenant</c> filter is
    /// untouched, so the enumeration stays inside the caller's own layer. Tenant isolation of the grants
    /// themselves comes from the rows: <c>AbpResourcePermissionGrants</c> is <c>IMultiTenant</c>, so a Host-layer
    /// grant is invisible to a tenant caller without a hand-written predicate.
    /// </para>
    /// <para>
    /// <c>IResourcePermissionStore.GetGrantedResourceKeysAsync</c> is deliberately not used in its place: it
    /// filters on resource + permission name only and is not per-user, so it would report every type that carries
    /// a grant for anyone — the trap #629 already recorded for the DTO populator.
    /// </para>
    /// </summary>
    protected virtual async Task<List<DocumentType>> GetLayerTypesAsync()
    {
        using (_dataFilter.Disable<ISoftDelete>())
        {
            return await _documentTypeRepository.GetListAsync();
        }
    }
}

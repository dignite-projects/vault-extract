using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Every per-type grant this caller holds, resolved once per request</b> (#635 decision 4). One read of the
/// layer's document types, then one <b>multi-name</b> <see cref="IResourcePermissionChecker"/> call per type —
/// the same overload ABP's own <c>ResourcePermissionPopulator</c> uses — and the answers are kept for the rest of
/// the request.
/// <para>
/// It replaces two hand-written serial sweeps (one per permission name, one per type) plus the second full sweep
/// the recycle-bin path used to do. The checker's single-document shape, its scope shape and the per-page rights
/// map all read this one map, so a list, its rights column and the detail page behind it cannot disagree, and a
/// request costs one grant check per type however many questions it asks.
/// </para>
/// <para>
/// <see cref="IScopedDependency"/>, not a cache with a key: the lifetime <i>is</i> the request, so there is no
/// invalidation to get wrong. Loading is lazy — a caller holding a module-wide permission short-circuits before
/// ever touching this map, and then no type sweep happens at all.
/// </para>
/// </summary>
public class DocumentTypeGrantMap : IScopedDependency
{
    /// <summary>
    /// The four names asked per type, in one call. Asking for all four regardless of which rule prompted the load
    /// is the point: the second question of the request is free, and it is what lets the per-row rights (six
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
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDataFilter _dataFilter;

    private Dictionary<Guid, HashSet<string>>? _granted;

    public DocumentTypeGrantMap(
        IResourcePermissionChecker resourcePermissionChecker,
        IDocumentTypeRepository documentTypeRepository,
        IDataFilter dataFilter)
    {
        _resourcePermissionChecker = resourcePermissionChecker;
        _documentTypeRepository = documentTypeRepository;
        _dataFilter = dataFilter;
    }

    /// <summary>Does the caller hold <paramref name="grant"/> on this one type?</summary>
    public virtual async Task<bool> HasAsync(Guid documentTypeId, string grant)
    {
        var granted = await EnsureLoadedAsync();
        return granted.TryGetValue(documentTypeId, out var names) && names.Contains(grant);
    }

    /// <summary>
    /// Every type of the layer the caller holds <paramref name="grant"/> on — the set a
    /// <see cref="DocumentAccessScope"/> is built from. Empty is a real answer, not a missing one.
    /// </summary>
    public virtual async Task<IReadOnlySet<Guid>> TypesWithAsync(string grant)
    {
        var granted = await EnsureLoadedAsync();

        var result = new HashSet<Guid>();
        foreach (var (typeId, names) in granted)
        {
            if (names.Contains(grant))
            {
                result.Add(typeId);
            }
        }

        return result;
    }

    /// <summary>
    /// Loads the map on first use and keeps it for the rest of the scope. A second question in the same request
    /// reads the dictionary and touches neither the type table nor the permission store.
    /// </summary>
    protected virtual async Task<Dictionary<Guid, HashSet<string>>> EnsureLoadedAsync()
    {
        if (_granted is not null)
        {
            return _granted;
        }

        var granted = new Dictionary<Guid, HashSet<string>>();

        foreach (var type in await GetLayerTypesAsync())
        {
            // ABP's multi-name overload: one store round trip (and one distributed-cache read) answers all four
            // grants for this type, instead of four. MultiplePermissionGrantResult reports Undefined for a miss,
            // which is not Granted -- fail-closed without an explicit else.
            var result = await _resourcePermissionChecker.IsGrantedAsync(
                AllGrants,
                VaultExtractResourcePermissions.Name,
                type.Id.ToString());

            HashSet<string>? names = null;
            foreach (var (name, grantResult) in result.Result)
            {
                if (grantResult == PermissionGrantResult.Granted)
                {
                    (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
                }
            }

            if (names is not null)
            {
                granted[type.Id] = names;
            }
        }

        return _granted = granted;
    }

    /// <summary>
    /// The layer's own document types — the set the sweep enumerates.
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

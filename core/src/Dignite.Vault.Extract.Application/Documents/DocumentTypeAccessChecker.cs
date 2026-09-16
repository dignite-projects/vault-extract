using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// One operation family's pair of names (#632 decision 2): the module-wide permission that admits every type of
/// the caller's layer, and the per-<c>DocumentType</c> resource permission that admits exactly one type.
/// <para>
/// This type exists so the table in the Issue has a single expression in code. A call site reads
/// <c>CheckAsync(DocumentAccessRule.Edit, document)</c> and cannot accidentally pair "edit" with the read
/// permission, which is what hand-writing the OR at nine call sites invites.
/// </para>
/// </summary>
public sealed record DocumentAccessRule(string ModuleWidePermission, string ResourcePermission)
{
    /// <summary>Detail, blob, list / export rows, pipeline runs, and the MCP paths that delegate to them.</summary>
    public static readonly DocumentAccessRule Read = new(
        VaultExtractPermissions.Documents.ReadAll,
        VaultExtractPermissions.DocumentTypes.Resources.Read);

    /// <summary>The operator edit family (confirm / reclassify / re-recognize / re-extract / update / reject / allow duplicate / resolve warnings).</summary>
    public static readonly DocumentAccessRule Edit = new(
        VaultExtractPermissions.Documents.ConfirmClassification,
        VaultExtractPermissions.DocumentTypes.Resources.Edit);

    /// <summary>Soft delete. Permanent delete stays module-wide only, by decision.</summary>
    public static readonly DocumentAccessRule Delete = new(
        VaultExtractPermissions.Documents.Delete,
        VaultExtractPermissions.DocumentTypes.Resources.Delete);

    /// <summary>
    /// Restore from the recycle bin, and reaching the recycle bin at all. <b>Whoever may delete may undo:</b> the
    /// per-type half is deliberately <see cref="VaultExtractPermissions.DocumentTypes.Resources.Delete"/> — the
    /// same grant <see cref="Delete"/> uses — rather than a fifth resource permission. Undoing an operation is not
    /// a wider right than the operation; a per-type deleter who could not restore would have to escalate a mistake
    /// of their own making to an admin, and a fifth frozen string buys nothing (#632).
    /// </summary>
    public static readonly DocumentAccessRule Restore = new(
        VaultExtractPermissions.Documents.Restore,
        VaultExtractPermissions.DocumentTypes.Resources.Delete);

    /// <summary>
    /// Declaring / assigning a type: <c>UploadAsync</c>'s <c>DocumentTypeId</c> and the <b>target</b> type of
    /// Confirm / Reclassify. The #629 rule, unchanged — it is about the type being assigned, never about the
    /// document's current type, which is <see cref="Edit"/>'s job.
    /// </summary>
    public static readonly DocumentAccessRule DeclareType = new(
        VaultExtractPermissions.Documents.ConfirmClassification,
        VaultExtractPermissions.DocumentTypes.Resources.Upload);
}

/// <summary>
/// The single implementation of the #632 rule: <b>an operation on a document is authorized by the module-wide
/// permission for that operation, OR by the matching grant on the document's current type.</b>
/// <para>
/// The OR has to be written by hand because ABP's <c>ResourcePermissionChecker</c> only consults the resource
/// value providers and never falls back to a module-wide permission (the commercial File Management module does
/// the same). #629 wrote it inline in <c>UploadAsync</c>; with four grants and a dozen enforcement points, one
/// inline copy per call site is how a screen and an endpoint quietly disagree — so there is exactly one copy,
/// here, and <c>UploadAsync</c> was moved onto it.
/// </para>
/// <para>
/// Every check is programmatic rather than an <c>[Authorize]</c> attribute, for the reason the read paths already
/// document: MCP / reflection / tool-dispatch paths do not run attributes. It is also structurally required —
/// <c>[Authorize(ConfirmClassification)]</c> would deny an Edit-grant holder before the method body could offer
/// the other half of the OR.
/// </para>
/// <para>
/// <b>Untyped documents are fail-closed.</b> A document with no <c>DocumentTypeId</c> (unclassified, failed
/// classification, container) belongs to no type, so no grant can ever cover it and only the module-wide
/// permission reaches it. Sub-documents carry their own type and are covered by it.
/// </para>
/// <para>
/// The per-type half runs through <see cref="IResourcePermissionChecker"/> directly rather than through
/// <c>AuthorizationService.IsGrantedAsync(entity, name)</c>. Both end at the same checker — ABP's keyed-object
/// requirement handler resolves this very service — but the direct call also serves the two shapes that have no
/// entity in hand (a document's type id, and the list-scope sweep), so all four shapes share one mechanism
/// instead of two. Tenant isolation comes from the grant rows themselves: <c>AbpResourcePermissionGrants</c> is
/// <c>IMultiTenant</c>, so a Host-layer grant is invisible to a tenant caller without a hand-written predicate.
/// </para>
/// </summary>
public class DocumentTypeAccessChecker : ITransientDependency
{
    private readonly IAuthorizationService _authorizationService;
    private readonly IResourcePermissionChecker _resourcePermissionChecker;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDataFilter _dataFilter;

    public DocumentTypeAccessChecker(
        IAuthorizationService authorizationService,
        IResourcePermissionChecker resourcePermissionChecker,
        IDocumentTypeRepository documentTypeRepository,
        IDataFilter dataFilter)
    {
        _authorizationService = authorizationService;
        _resourcePermissionChecker = resourcePermissionChecker;
        _documentTypeRepository = documentTypeRepository;
        _dataFilter = dataFilter;
    }

    /// <summary>
    /// The rule itself. <paramref name="documentTypeId"/> is the type the operation is judged against — a
    /// document's current type for Read / Edit / Delete, the target type for
    /// <see cref="DocumentAccessRule.DeclareType"/>. <c>null</c> means an untyped document and reduces the rule
    /// to the module-wide permission alone.
    /// </summary>
    public virtual async Task<bool> IsGrantedAsync(DocumentAccessRule rule, Guid? documentTypeId)
    {
        if (await _authorizationService.IsGrantedAsync(rule.ModuleWidePermission))
        {
            return true;
        }

        if (!documentTypeId.HasValue)
        {
            // Fail-closed: no type, therefore no grant can name it.
            return false;
        }

        return await _resourcePermissionChecker.IsGrantedAsync(
            rule.ResourcePermission,
            VaultExtractPermissions.DocumentTypes.Resources.Name,
            documentTypeId.Value.ToString());
    }

    /// <summary>Asserts <see cref="IsGrantedAsync(DocumentAccessRule, Guid?)"/>, throwing <see cref="AbpAuthorizationException"/>.</summary>
    public virtual async Task CheckAsync(DocumentAccessRule rule, Guid? documentTypeId)
    {
        if (!await IsGrantedAsync(rule, documentTypeId))
        {
            throw new AbpAuthorizationException();
        }
    }

    /// <summary>
    /// Single-document shape: judges the operation against the document's <b>current</b> type. Call it after the
    /// document has been loaded, so a missing / cross-layer id still fails with <c>EntityNotFoundException</c>
    /// first — the #629 existence-before-permission ordering, kept unchanged.
    /// </summary>
    public virtual Task CheckAsync(DocumentAccessRule rule, Document document)
    {
        return CheckAsync(rule, document.DocumentTypeId);
    }

    /// <summary>
    /// Declare-a-type shape: judges the operation against a <b>target</b> <see cref="DocumentType"/> the caller
    /// is assigning, not against any document. Takes the loaded entity rather than an id precisely because the
    /// caller must have resolved it under the ambient <c>IMultiTenant</c> filter first.
    /// </summary>
    public virtual Task CheckTargetTypeAsync(DocumentAccessRule rule, DocumentType targetType)
    {
        return CheckAsync(rule, targetType.Id);
    }

    /// <summary>
    /// Layer-scope shape: does the caller hold this rule's module-wide permission, or its resource permission on
    /// <b>at least one</b> type of the layer? That is the question an entry gate asks — "may this caller do this
    /// at all?" — as opposed to <see cref="IsGrantedAsync(DocumentAccessRule, Guid?)"/>'s "may they do it to this
    /// one document?".
    /// <para>
    /// It exists for the recycle bin (#632): that branch of the list used to assert the module-wide
    /// <c>Documents.Restore</c> outright, which would shut a per-type deleter out of the page and leave the
    /// Restore rule unreachable from the UI. It decides admission only — the rows inside stay narrowed by
    /// <see cref="GetReadableDocumentTypeIdsAsync"/>, so a caller who may delete type A but not read it is
    /// admitted to an empty recycle bin, which is the intended fail-closed answer rather than a special case.
    /// </para>
    /// </summary>
    public virtual async Task<bool> IsGrantedOnAnyTypeAsync(DocumentAccessRule rule)
    {
        if (await _authorizationService.IsGrantedAsync(rule.ModuleWidePermission))
        {
            return true;
        }

        foreach (var type in await GetLayerTypesAsync())
        {
            if (await _resourcePermissionChecker.IsGrantedAsync(
                    rule.ResourcePermission,
                    VaultExtractPermissions.DocumentTypes.Resources.Name,
                    type.Id.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Asserts <see cref="IsGrantedOnAnyTypeAsync"/>, throwing <see cref="AbpAuthorizationException"/>.</summary>
    public virtual async Task CheckOnAnyTypeAsync(DocumentAccessRule rule)
    {
        if (!await IsGrantedOnAnyTypeAsync(rule))
        {
            throw new AbpAuthorizationException();
        }
    }

    /// <summary>
    /// List-scope shape (#632 decision 3): the document types the caller may read, or <c>null</c> when the caller
    /// holds <c>Documents.ReadAll</c> and the scope is therefore unrestricted.
    /// <para>
    /// An empty (non-null) result is a real answer — "this caller may read nothing" — and the query chain turns
    /// it into an empty page, not into an absent filter. Untyped rows are excluded by construction: the returned
    /// set only ever contains type ids.
    /// </para>
    /// <para>
    /// Resolved once per request over the layer's own types (tens, and each check is a distributed-cache read),
    /// soft delete traversed — see the body for why.
    /// <c>IResourcePermissionStore.GetGrantedResourceKeysAsync</c> is deliberately not used: it filters on
    /// resource + permission name only and is not per-user, so it would report every type that carries a grant
    /// for anyone — the same trap #629 recorded for the DTO populator.
    /// </para>
    /// </summary>
    public virtual async Task<IReadOnlyCollection<Guid>?> GetReadableDocumentTypeIdsAsync()
    {
        if (await _authorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.ReadAll))
        {
            return null;
        }

        var types = await GetLayerTypesAsync();

        var readable = new List<Guid>(types.Count);
        foreach (var type in types)
        {
            if (await _resourcePermissionChecker.IsGrantedAsync(
                    VaultExtractPermissions.DocumentTypes.Resources.Read,
                    VaultExtractPermissions.DocumentTypes.Resources.Name,
                    type.Id.ToString()))
            {
                readable.Add(type.Id);
            }
        }

        return readable;
    }

    /// <summary>
    /// The layer's own document types — the set both per-type sweeps enumerate.
    /// <para>
    /// Soft delete is traversed on purpose, the same way <c>DocumentAppService.ResolveReferenceMapsAsync</c>
    /// traverses it: a document classified to a since-archived type is still in the list of a
    /// <c>Documents.ReadAll</c> holder, so a caller holding an explicit Read grant on that type must see it too —
    /// otherwise the narrow caller and the module-wide caller disagree about which rows exist. It also makes the
    /// sweep independent of whether the CALLER happens to be inside <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c>
    /// (the recycle-bin branch of the list is), which would otherwise silently widen or narrow it depending on the
    /// call site.
    /// </para>
    /// <para>
    /// This can only ever ADD a type the caller was explicitly granted; the <c>IMultiTenant</c> filter is
    /// untouched, so the enumeration stays inside the caller's own layer.
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

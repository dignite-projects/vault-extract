using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The single implementation of the #635 rule: <b>an operation on a document is authorized by
/// <c>Documents.Default</c> (entry) AND either the module-wide permission for that operation, OR the caller owns
/// the document and the rule allows it, OR the matching grant on the subject's type.</b> The rows live in
/// <see cref="DocumentAccessRule"/>; this class is the only place that evaluates them.
/// <para>
/// <b>Two shapes, not five.</b> <see cref="IsGrantedAsync"/> / <see cref="CheckAsync"/> answer "may this caller
/// do this to this one subject"; <see cref="ResolveScopeAsync"/> answers "what may this caller reach at all",
/// as a <see cref="DocumentAccessScope"/> that is both a predicate and a membership test. #632's
/// <c>CheckTargetTypeAsync</c>, <c>IsGrantedOnAnyTypeAsync</c>, <c>CheckOnAnyTypeAsync</c> and
/// <c>GetReadableDocumentTypeIdsAsync</c> are gone: the first became a <see cref="DocumentAccessSubject"/>, the
/// other three became this scope. Each of them had handled entry differently — two asserted it, one deliberately
/// did not — and every new way of asking had produced a new method with its own answer to the same question.
/// </para>
/// <para>
/// <b>Entry is asserted in exactly one place</b>, <see cref="IsEntryGrantedAsync"/>, reached by both shapes.
/// <see cref="CheckAsync"/> throws without it and <see cref="ResolveScopeAsync"/> throws without it, so there is
/// no longer any way to ask this class a question and get a permissive answer for a caller who may not open the
/// documents area. That is what closed the escapes #635 catalogued: <c>PermanentDeleteAsync</c>,
/// <c>RetryPipelineAsync</c>, the four <c>Reprocessing</c> methods, <c>ExportAsync</c> and the untyped
/// <c>UploadAsync</c> branch carried only their own <c>[Authorize]</c> and asserted entry nowhere.
/// </para>
/// <para>
/// Every check is programmatic rather than an <c>[Authorize]</c> attribute, for the reason the read paths already
/// document: MCP / reflection / tool-dispatch paths do not run attributes. It is also structurally required — an
/// attribute fires before the method body and would deny a per-type grant holder, or an owner, before the body
/// could offer the other arms of the OR.
/// </para>
/// <para>
/// <b>Ownership is a per-rule flag, never a global arm.</b> <see cref="DocumentAccessRule.Review"/> carries the
/// same two permission names as <see cref="DocumentAccessRule.Edit"/> and differs only in that flag, because its
/// three methods clear a blocking review reason and that gate exists so somebody other than the uploader checks
/// the uploader's work.
/// </para>
/// <para>
/// The per-type arm runs through <see cref="DocumentTypeGrantMap"/> rather than through
/// <c>AuthorizationService.IsGrantedAsync(entity, name)</c>. Both end at ABP's own
/// <c>IResourcePermissionChecker</c> — the keyed-object requirement handler resolves that very service — but the
/// map also serves the two shapes that have no entity in hand, answers all four grants per type in one call, and
/// holds the answers for the rest of the request.
/// </para>
/// </summary>
public class DocumentTypeAccessChecker : ITransientDependency
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ICurrentUser _currentUser;
    private readonly DocumentTypeGrantMap _grantMap;

    public DocumentTypeAccessChecker(
        IAuthorizationService authorizationService,
        ICurrentUser currentUser,
        DocumentTypeGrantMap grantMap)
    {
        _authorizationService = authorizationService;
        _currentUser = currentUser;
        _grantMap = grantMap;
    }

    /// <summary>
    /// The precondition every rule shares: <c>Documents.Default</c>, which since #632 decision 1 means
    /// <b>entry</b> — "may enter the documents area <i>and</i> use it within their own scope". Not a synonym for
    /// reading everything (that is <c>Documents.ReadAll</c>), and not merely the SPA route gate.
    /// <para>
    /// <b>Entry gates the module-wide arm as well as the other two</b>, deliberately. The alternative — requiring
    /// entry only when the caller leans on a grant or on ownership — would say that a principal holding
    /// <c>Documents.Delete</c> but not <c>Documents.Default</c> may soft-delete documents in an area it may not
    /// open. Three further reasons it is the only consistent choice:
    /// <list type="number">
    /// <item>Read already behaved this way before #635, so leaving the other families more permissive than the
    /// read family would be backwards.</item>
    /// <item>The definition provider makes every one of these module-wide permissions a CHILD of
    /// <c>Documents.Default</c>. ABP's dialog grants the parent with the child, so a real principal carries both;
    /// only a programmatic <c>IPermissionManager</c> grant or a hand-edited store can separate them, because
    /// <c>PermissionChecker</c> never consults <c>Parent</c> at check time. Requiring entry makes the check agree
    /// with the definition the dialog enforces instead of relying on the grant path to have been the dialog.</item>
    /// <item>It closes a hole ordinary administration opens: handing out per-type grants is gated by
    /// <c>DocumentTypes.ManagePermissions</c> alone, which is unrelated to <c>Documents.*</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    protected virtual Task<bool> IsEntryGrantedAsync()
    {
        return _authorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default);
    }

    /// <summary>
    /// The rule itself, for one subject: a loaded document
    /// (<see cref="DocumentAccessSubject.Of(Document)"/>), a target type being assigned
    /// (<see cref="DocumentAccessSubject.OfType(Guid)"/>), or nothing at all
    /// (<see cref="DocumentAccessSubject.None"/>) for a family whose rule reads neither fact.
    /// </summary>
    public virtual async Task<bool> IsGrantedAsync(DocumentAccessRule rule, DocumentAccessSubject subject)
    {
        if (!await IsEntryGrantedAsync())
        {
            return false;
        }

        if (await _authorizationService.IsGrantedAsync(rule.ModuleWidePermission))
        {
            return true;
        }

        // Ownership. A subject with no CreatorId (a machine-created row, a pre-#635 derived sub-document) never
        // matches, and neither does a caller with no user id -- a client-credentials principal has no `sub`, so
        // its Id is null and the arm is structurally unreachable for it.
        if (rule.OwnerMayPerform && subject.CreatorId is { } creatorId && _currentUser.Id == creatorId)
        {
            return true;
        }

        // The per-type arm. A rule with no resource permission is module-wide only by decision, and an untyped
        // subject belongs to no type, so no grant can name it: both fall through to false, fail-closed.
        if (rule.ResourcePermission is { } resourcePermission && subject.DocumentTypeId is { } documentTypeId)
        {
            return await _grantMap.HasAsync(documentTypeId, resourcePermission);
        }

        return false;
    }

    /// <summary>Asserts <see cref="IsGrantedAsync"/>, throwing <see cref="AbpAuthorizationException"/>.</summary>
    public virtual async Task CheckAsync(DocumentAccessRule rule, DocumentAccessSubject subject)
    {
        if (!await IsGrantedAsync(rule, subject))
        {
            throw new AbpAuthorizationException();
        }
    }

    /// <summary>
    /// The scope shape: everything this caller may reach under <paramref name="rule"/>, as one object that is
    /// both a query predicate and a membership test. It <b>throws</b> without entry, exactly as
    /// <see cref="CheckAsync"/> does — a scope resolver that silently answered "you reach nothing" for a caller
    /// with no entry is how the export and the recycle bin ended up admitting principals the single-document
    /// shapes refused.
    /// <para>
    /// Used as the admission gate wherever a caller must be judged <b>before</b> a document is loaded (the read
    /// paths, so existence is not disclosed to a caller with no entry), and as the row filter wherever a set of
    /// documents is returned.
    /// </para>
    /// </summary>
    public virtual async Task<DocumentAccessScope> ResolveScopeAsync(DocumentAccessRule rule)
    {
        if (!await IsEntryGrantedAsync())
        {
            throw new AbpAuthorizationException();
        }

        if (await _authorizationService.IsGrantedAsync(rule.ModuleWidePermission))
        {
            return DocumentAccessScope.Unrestricted;
        }

        var types = rule.ResourcePermission is { } resourcePermission
            ? await _grantMap.TypesWithAsync(resourcePermission)
            : EmptyTypeSet;

        return DocumentAccessScope.Of(types, rule.OwnerMayPerform ? _currentUser.Id : null);
    }

    private static readonly IReadOnlySet<Guid> EmptyTypeSet = new HashSet<Guid>();
}

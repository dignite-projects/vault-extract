using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The single implementation of the #635 rule: <b>an operation on a document is authorized by
/// <c>Documents.Default</c> (entry) AND either a module-wide permission of that operation's role-level set, OR the
/// caller owns the document and the rule allows it, OR the matching grant on the subject's type.</b> The rows live in
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
/// <b>Ownership is a per-rule, three-valued arm, never a global one</b> (<see cref="DocumentOwnerArm"/>).
/// <see cref="DocumentAccessRule.Review"/> carries the same two permission names as
/// <see cref="DocumentAccessRule.Edit"/> and differs only in that value, because its three methods clear a
/// blocking review reason and that gate exists so somebody other than the uploader checks the uploader's work —
/// and <see cref="DocumentAccessRule.Edit"/> itself is <see cref="DocumentOwnerArm.UnlessUnderReview"/>, because
/// the edit family clears the same bits as a side effect and would otherwise reopen the door one along.
/// </para>
/// <para>
/// The per-type arm runs through <see cref="DocumentAccessMemo"/> rather than through
/// <c>AuthorizationService.IsGrantedAsync(entity, name)</c>. Both end at ABP's own
/// <c>IResourcePermissionChecker</c> — the keyed-object requirement handler resolves that very service — but the
/// memo also serves the two shapes that have no entity in hand, answers all four grants per type in one call, and
/// holds the answers for the rest of the request.
/// </para>
/// </summary>
public class DocumentAccessChecker : ITransientDependency
{
    private readonly ICurrentUser _currentUser;
    private readonly DocumentAccessMemo _accessMemo;

    public DocumentAccessChecker(
        ICurrentUser currentUser,
        DocumentAccessMemo accessMemo)
    {
        _currentUser = currentUser;
        _accessMemo = accessMemo;
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
    /// <item>The definition provider makes every one of these module-wide permissions a DESCENDANT of
    /// <c>Documents.Default</c> — most as children, <c>Pipelines.Retry</c> and <c>Reprocessing.*</c> as
    /// grandchildren. ABP's dialog grants the whole ancestor chain of a ticked permission, so a real principal
    /// carries entry; only a programmatic <c>IPermissionManager</c> grant or a hand-edited store can separate them, because
    /// <c>PermissionChecker</c> never consults <c>Parent</c> at check time. Requiring entry makes the check agree
    /// with the definition the dialog enforces instead of relying on the grant path to have been the dialog.</item>
    /// <item>It closes a hole ordinary administration opens: handing out per-type grants is gated by
    /// <c>DocumentTypes.ManagePermissions</c> alone, which is unrelated to <c>Documents.*</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    protected virtual Task<bool> IsEntryGrantedAsync()
    {
        return _accessMemo.IsPermissionGrantedAsync(VaultExtractPermissions.Documents.Default);
    }

    /// <summary>
    /// Asserts entry on its own, for the call sites that must refuse a caller <b>before</b> loading a document:
    /// otherwise an unauthenticated caller tells a real id from an unknown one by whether it gets 404 or 403.
    /// Every mutating method calls it first; the read paths call it and then judge the loaded document with
    /// <see cref="IsGrantedAsync"/>.
    /// <para>
    /// It is not a third shape — it asserts exactly <see cref="IsEntryGrantedAsync"/>, the same single
    /// precondition <see cref="IsGrantedAsync"/> and <see cref="ResolveScopeAsync"/> evaluate, and decides
    /// nothing else. It exists because the alternative (resolving a whole scope to throw on its entry check)
    /// made a single-document read sweep the layer's types for no other reason.
    /// </para>
    /// </summary>
    public virtual async Task CheckEntryAsync()
    {
        if (!await IsEntryGrantedAsync())
        {
            throw new AbpAuthorizationException();
        }
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

        if (await IsModuleWideGrantedAsync(rule))
        {
            return true;
        }

        // Ownership. A subject with no CreatorId (a machine-created row, a pre-#635 derived sub-document) never
        // matches, and neither does a caller with no user id -- a client-credentials principal has no `sub`, so
        // its Id is null and the arm is structurally unreachable for it.
        if (subject.CreatorId is { } creatorId && _currentUser.Id == creatorId && IsOwnerArmOpen(rule, subject))
        {
            return true;
        }

        // The per-type arm. A rule with no resource permission is module-wide only by decision, and an untyped
        // subject belongs to no type, so no grant can name it: both fall through to false, fail-closed.
        if (rule.ResourcePermission is { } resourcePermission && subject.DocumentTypeId is { } documentTypeId)
        {
            return await _accessMemo.HasAsync(documentTypeId, resourcePermission);
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
    /// Used wherever a <b>set</b> of documents is returned: the operator list, the recycle bin, the export and
    /// the duplicate-candidate panel. A single-document check uses <see cref="CheckEntryAsync"/> plus
    /// <see cref="IsGrantedAsync"/> instead, which costs one grant check rather than a sweep of the layer.
    /// </para>
    /// <para>
    /// <b>Only rules whose owner arm is unconditional can have a scope.</b> A scope is a row predicate, and a
    /// row predicate has no term for "is this document under review" — the review state is on the row, not on
    /// the caller. Asking for the scope of a rule whose owner arm is
    /// <see cref="DocumentOwnerArm.UnlessUnderReview"/> would therefore silently widen it to every document the
    /// caller uploaded, locked ones included. It throws instead. In practice only
    /// <see cref="DocumentAccessRule.Read"/> is ever asked, and its arm is unconditional by design.
    /// </para>
    /// </summary>
    public virtual async Task<DocumentAccessScope> ResolveScopeAsync(DocumentAccessRule rule)
    {
        if (rule.OwnerArm == DocumentOwnerArm.UnlessUnderReview)
        {
            throw new AbpException(
                $"A scope cannot be resolved for a rule whose owner arm is " +
                $"{nameof(DocumentOwnerArm.UnlessUnderReview)} ({string.Join(" | ", rule.ModuleWidePermissions)}): the scope is a row " +
                $"predicate and has no review-state term, so it would silently admit the caller's own documents " +
                $"that are locked for review. Use CheckEntryAsync + IsGrantedAsync per document instead.");
        }

        if (!await IsEntryGrantedAsync())
        {
            throw new AbpAuthorizationException();
        }

        if (await IsModuleWideGrantedAsync(rule))
        {
            return DocumentAccessScope.Unrestricted;
        }

        var types = rule.ResourcePermission is { } resourcePermission
            ? await _accessMemo.TypesWithAsync(resourcePermission)
            : EmptyTypeSet;

        return DocumentAccessScope.Of(
            types, rule.OwnerArm == DocumentOwnerArm.Always ? _currentUser.Id : null);
    }

    /// <summary>
    /// The role-level arm: <b>any</b> member of <see cref="DocumentAccessRule.ModuleWidePermissions"/> admits
    /// (#645). Asked in the order the row writes them and stopping at the first granted one, sequentially rather
    /// than in parallel for the reason <see cref="DocumentAccessMemo"/> gives. Each name goes through the memo, so
    /// a name two rules share — <c>ConfirmClassification</c> is on Edit, Review and DeclareType — still costs one
    /// check per request however many rules ask it.
    /// </summary>
    protected virtual async Task<bool> IsModuleWideGrantedAsync(DocumentAccessRule rule)
    {
        foreach (var permissionName in rule.ModuleWidePermissions)
        {
            if (await _accessMemo.IsPermissionGrantedAsync(permissionName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The owner arm's per-rule verdict for one subject — see <see cref="DocumentOwnerArm"/>. Kept beside the
    /// arm it evaluates so "which rules an owner reaches, and when" has exactly one reading.
    /// </summary>
    protected virtual bool IsOwnerArmOpen(DocumentAccessRule rule, DocumentAccessSubject subject)
        => rule.OwnerArm switch
        {
            DocumentOwnerArm.Always => true,
            DocumentOwnerArm.UnlessUnderReview => !subject.UnderReview,
            _ => false
        };

    private static readonly IReadOnlySet<Guid> EmptyTypeSet = new HashSet<Guid>();
}

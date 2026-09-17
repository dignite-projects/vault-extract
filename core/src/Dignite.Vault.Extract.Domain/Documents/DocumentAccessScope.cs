using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Volo.Abp;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>What this caller may reach</b>, as one object (#635 decision 3). It replaces the nullable
/// <c>IReadOnlyCollection&lt;Guid&gt;?</c> that carried three meanings at once (<c>null</c> unrestricted, empty
/// "nothing", non-empty "these") and made every consumer branch on emptiness for itself.
/// <para>
/// Non-nullable by construction. <see cref="Unrestricted"/> is the module-wide holder; <see cref="Of"/> carries
/// the types the caller reaches on the per-type arm <b>and</b> the caller's own user id when the rule allows
/// ownership. An <see cref="Of"/> with neither — a machine identity with no grants — yields an always-false
/// predicate <b>inside this object</b>, which is what retires <c>Where(d =&gt; false)</c> and its five-line
/// comment from <see cref="DocumentQueries.ApplyMetadataFilter"/>.
/// </para>
/// <para>
/// It is the single source for the list predicate, the export's row narrowing, the recycle bin's rows, the
/// per-page rights map and <c>GetListAsync</c>'s type-code short circuit, so "download the current view", "search
/// over MCP" and "look at the screen" cannot disagree about which rows exist.
/// </para>
/// <para>
/// Domain, not Application: <see cref="DocumentQueries.ApplyMetadataFilter"/> lives here and needs
/// <see cref="ToPredicate"/>. Nothing about permissions leaks down with it — a scope knows type ids and one owner
/// id, never a permission name.
/// </para>
/// </summary>
public sealed class DocumentAccessScope
{
    /// <summary>
    /// Every document of the layer, including untyped ones: the caller holds the rule's module-wide permission.
    /// The predicate is absent rather than universally true, so the query chain adds no <c>WHERE</c> at all.
    /// </summary>
    public static readonly DocumentAccessScope Unrestricted = new(documentTypeIds: null, ownerId: null);

    /// <summary>
    /// A narrowed scope: the types the caller holds this rule's grant on, plus — when the rule's
    /// <c>OwnerMayPerform</c> is set and the caller is a real user — the caller's own id. Either may be empty /
    /// null; both empty is a real answer ("this caller reaches nothing") and is handled inside, not by the caller.
    /// </summary>
    public static DocumentAccessScope Of(IReadOnlySet<Guid> documentTypeIds, Guid? ownerId)
        => new(Check.NotNull(documentTypeIds, nameof(documentTypeIds)), ownerId);

    private readonly IReadOnlySet<Guid>? _documentTypeIds;
    private readonly Guid? _ownerId;

    private DocumentAccessScope(IReadOnlySet<Guid>? documentTypeIds, Guid? ownerId)
    {
        _documentTypeIds = documentTypeIds;
        _ownerId = ownerId;
    }

    /// <summary>Whether this is <see cref="Unrestricted"/> — the module-wide holder, no predicate at all.</summary>
    public bool IsUnrestricted => _documentTypeIds is null;

    /// <summary>
    /// The row predicate: <c>DocumentTypeId ∈ types OR CreatorId == ownerId</c>, with each absent arm dropped and
    /// <b>both</b> absent collapsing to a constant false. Untyped rows are reached only through the owner arm —
    /// they belong to no type, so no grant can name them.
    /// <para>
    /// <see cref="IsUnrestricted"/> returns a constant-true expression; callers that can skip the
    /// <c>Where</c> entirely (the shared metadata chain does) should test <see cref="IsUnrestricted"/> instead of
    /// handing a tautology to the provider.
    /// </para>
    /// </summary>
    public Expression<Func<Document, bool>> ToPredicate()
    {
        if (_documentTypeIds is null)
        {
            return _ => true;
        }

        // Typed as IReadOnlyCollection<Guid> on purpose: Contains then binds to Enumerable.Contains, the exact
        // shape the relational provider already translated for the pre-#635 read scope. IReadOnlySet<T>.Contains
        // is an interface method the provider has no reason to know.
        IReadOnlyCollection<Guid> types = _documentTypeIds;

        if (_ownerId is not { } owner)
        {
            return types.Count == 0
                ? _ => false
                : d => d.DocumentTypeId.HasValue && types.Contains(d.DocumentTypeId.Value);
        }

        return types.Count == 0
            ? d => d.CreatorId == owner
            : d => (d.DocumentTypeId.HasValue && types.Contains(d.DocumentTypeId.Value)) || d.CreatorId == owner;
    }

    /// <summary>
    /// The same judgment for one already-loaded subject, so a single-document check and a list predicate cannot
    /// drift: <c>true</c> exactly when <see cref="ToPredicate"/> would keep that row.
    /// </summary>
    public bool Allows(DocumentAccessSubject subject)
    {
        if (_documentTypeIds is null)
        {
            return true;
        }

        if (subject.DocumentTypeId is { } typeId && _documentTypeIds.Contains(typeId))
        {
            return true;
        }

        return _ownerId is { } owner && subject.CreatorId == owner;
    }

    /// <summary>
    /// Could this scope produce <b>any</b> row of one specific type? Unrestricted, or the type is granted, or the
    /// scope carries an owner arm and the caller may own documents of that type.
    /// <para>
    /// This is the question <c>GetListAsync</c>'s requested <c>DocumentTypeCode</c> and the export's single-type
    /// gate ask, and it is deliberately weaker than <c>Allows(new DocumentAccessSubject(typeId, null))</c>: that
    /// form answers "may I reach a document of this type that <i>nobody</i> owns", which is false for an
    /// owner-armed caller and would hide an uploader's own documents the moment they filtered the list by their
    /// own type — exactly the day-one failure #635 exists to fix.
    /// </para>
    /// </summary>
    public bool AllowsAnyOfType(Guid documentTypeId)
        => GrantsWholeType(documentTypeId) || _ownerId is not null;

    /// <summary>
    /// Does this scope reach <b>every</b> document of one type — unrestricted, or granted on that type — as
    /// opposed to only the caller's own?
    /// <para>
    /// The distinction decides what may be <i>said</i> about a type rather than which rows come back. A caller
    /// who reaches a type only through the ownership arm holds no grant on it, so describing its schema to them
    /// (the unknown-field error naming what the type does and does not define) discloses something the rows
    /// themselves never would.
    /// </para>
    /// </summary>
    public bool GrantsWholeType(Guid documentTypeId)
        => _documentTypeIds is null || _documentTypeIds.Contains(documentTypeId);
}

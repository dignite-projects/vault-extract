using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 3: the read scope as a predicate on the shared
/// <see cref="DocumentQueries.ApplyMetadataFilter"/> chain — the one place the operator list, the export and the
/// MCP search all pass through.
/// <para>
/// A pure function over an in-memory queryable, deliberately not through EF: what is under test here is the
/// <b>chain's</b> handling of the scope — that <see cref="DocumentAccessScope.Unrestricted"/> adds no predicate,
/// that a narrowed scope AND-combines with the other filters rather than replacing them, and that a scope which
/// reaches nothing yields nothing rather than degrading into an absent filter. The predicate's own translation to
/// SQL is pinned separately, against a real provider, in
/// <c>EntityFrameworkCore.Tests/Documents/DocumentAccessScopePredicate_Tests</c> — an in-memory
/// <c>AsQueryable()</c> would happily evaluate an expression no relational provider can translate.
/// </para>
/// </summary>
public class DocumentReadScopeFilter_Tests
{
    private static readonly Guid TypeA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000632");
    private static readonly Guid TypeB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000632");
    private static readonly Guid Owner = Guid.Parse("cccccccc-0000-0000-0000-000000000635");
    private static readonly Guid Stranger = Guid.Parse("dddddddd-0000-0000-0000-000000000635");

    [Fact]
    public void An_unrestricted_scope_is_an_absent_filter_and_keeps_every_row_including_untyped_ones()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = DocumentAccessScope.Unrestricted
        }).ToList();

        rows.Count.ShouldBe(5);
    }

    /// <summary>
    /// The default matters: every other member of the filter is "absent means no filter", and the scope now
    /// reads the same way instead of being a nullable third state a caller could forget to set.
    /// </summary>
    [Fact]
    public void The_default_scope_is_unrestricted()
    {
        new DocumentMetadataFilter().ReadScope.ShouldBeSameAs(DocumentAccessScope.Unrestricted);
        DocumentAccessScope.Unrestricted.IsUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public void A_type_only_scope_keeps_only_its_own_types_rows()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = Scope(ownerId: null, TypeA)
        }).ToList();

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(d => d.DocumentTypeId == TypeA);
    }

    /// <summary>
    /// The #635 arm. The owner reaches every row they uploaded — including the untyped one, which no grant can
    /// ever name — on top of whatever types they were granted.
    /// </summary>
    [Fact]
    public void An_owner_arm_adds_the_callers_own_rows_including_untyped_ones()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = Scope(Owner, TypeA)
        }).ToList();

        // Two type-A rows (one of them the owner's), the owner's type-B row, and the owner's untyped row.
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(d => d.DocumentTypeId == TypeA || d.CreatorId == Owner);
        rows.ShouldContain(d => d.DocumentTypeId == null && d.CreatorId == Owner);
    }

    /// <summary>
    /// Ownership without any grant: an uploader holding entry and an Upload grant and nothing else. They see
    /// exactly what they uploaded, typed or not, and nothing else — the day-one shape #635 exists to make work.
    /// </summary>
    [Fact]
    public void An_owner_only_scope_keeps_exactly_the_callers_own_rows()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = Scope(Owner)
        }).ToList();

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(d => d.CreatorId == Owner);
    }

    /// <summary>
    /// Neither arm: a machine identity with no grants, which has no <c>sub</c> and therefore no owner id either.
    /// The scope carries its own always-false predicate, so the chain narrows to nothing without any consumer
    /// branching on emptiness — the <c>Where(d =&gt; false)</c> special case is gone from the chain, not moved.
    /// </summary>
    [Fact]
    public void A_scope_with_neither_arm_reaches_nothing()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = Scope(ownerId: null)
        }).ToList();

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// Untyped rows belong to no type, so a grant can never name them: a caller with every type in scope and no
    /// ownership of that row still does not get it.
    /// </summary>
    [Fact]
    public void Untyped_rows_stay_out_of_a_type_only_scope_even_when_every_type_is_in_it()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadScope = Scope(ownerId: null, TypeA, TypeB)
        }).ToList();

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(d => d.DocumentTypeId.HasValue);
    }

    [Fact]
    public void The_scope_combines_with_the_other_predicates_rather_than_replacing_them()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            DocumentTypeId = TypeB,
            ReadScope = Scope(ownerId: null, TypeA)
        }).ToList();

        // The requested type is outside the caller's scope: AND-combined, so nothing comes back rather than the
        // later predicate winning.
        rows.ShouldBeEmpty();
    }

    // ===================== Allows / AllowsAnyOfType agree with the predicate =====================

    /// <summary>
    /// <see cref="DocumentAccessScope.Allows"/> is the single-document twin of <see cref="DocumentAccessScope.ToPredicate"/>
    /// and must answer identically for every row, or the detail page and the list disagree about the same document.
    /// </summary>
    [Fact]
    public void Allows_answers_exactly_what_the_predicate_keeps()
    {
        DocumentAccessScope[] scopes =
        [
            DocumentAccessScope.Unrestricted,
            Scope(ownerId: null),
            Scope(ownerId: null, TypeA),
            Scope(Owner),
            Scope(Owner, TypeA),
            Scope(Stranger, TypeA, TypeB)
        ];

        foreach (var scope in scopes)
        {
            var kept = Query(Documents()).Where(scope.ToPredicate()).Select(d => d.Id).ToHashSet();

            foreach (var document in Documents())
            {
                scope.Allows(DocumentAccessSubject.Of(document))
                    .ShouldBe(kept.Contains(document.Id), $"scope disagreed about {document.Id}");
            }
        }
    }

    /// <summary>
    /// <see cref="DocumentAccessScope.AllowsAnyOfType"/> is weaker than <c>Allows</c> on a creator-less subject,
    /// and deliberately so: an owner-armed caller may hold rows of a type they were never granted, so the list's
    /// type-code short circuit must not treat that type as unreachable.
    /// </summary>
    [Fact]
    public void AllowsAnyOfType_admits_an_ungranted_type_for_an_owner_armed_scope()
    {
        Scope(Owner).AllowsAnyOfType(TypeB).ShouldBeTrue();
        Scope(Owner).Allows(new DocumentAccessSubject(TypeB, CreatorId: null)).ShouldBeFalse();

        Scope(ownerId: null, TypeA).AllowsAnyOfType(TypeB).ShouldBeFalse();
        Scope(ownerId: null, TypeA).AllowsAnyOfType(TypeA).ShouldBeTrue();
        DocumentAccessScope.Unrestricted.AllowsAnyOfType(TypeB).ShouldBeTrue();
    }

    private static DocumentAccessScope Scope(Guid? ownerId, params Guid[] documentTypeIds)
        => DocumentAccessScope.Of(documentTypeIds.ToHashSet(), ownerId);

    private static IQueryable<Document> Query(IEnumerable<Document> documents) => documents.AsQueryable();

    /// <summary>
    /// Five rows with stable ids, so <see cref="Allows_answers_exactly_what_the_predicate_keeps"/> can compare two
    /// independently built sequences row by row.
    /// </summary>
    private static List<Document> Documents() =>
    [
        NewDocument(1, TypeA, Owner),
        NewDocument(2, TypeA, Stranger),
        NewDocument(3, TypeB, Owner),
        NewDocument(4, documentTypeId: null, creatorId: Owner),
        NewDocument(5, documentTypeId: null, creatorId: null)
    ];

    private static Document NewDocument(int seed, Guid? documentTypeId, Guid? creatorId)
    {
        var document = new Document(
            new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "tester",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "scope.pdf"));

        if (documentTypeId.HasValue)
        {
            // DocumentTypeId is written by the classification pipeline through an internal surface; reflection
            // stands in for it so this stays a pure predicate test with no manager / DI container involved.
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        if (creatorId.HasValue)
        {
            // CreatorId is ABP's audit property (protected set, written by AuditPropertySetter at insert).
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        return document;
    }
}

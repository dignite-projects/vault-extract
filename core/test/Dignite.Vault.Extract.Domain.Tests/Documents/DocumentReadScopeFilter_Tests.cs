using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #632 decision 3: the per-document-type read scope, as a predicate on the shared
/// <see cref="DocumentQueries.ApplyMetadataFilter"/> chain — the one place the operator list, the export and the
/// MCP search all pass through.
/// <para>
/// A pure function over an in-memory queryable, deliberately not through EF: what is under test is the predicate's
/// <b>semantics</b>, and in particular the three-state distinction that makes it different from every other member
/// of <see cref="DocumentMetadataFilter"/> — <c>null</c> is "no filter", an <b>empty</b> set is "may read nothing",
/// and untyped rows are excluded by construction rather than by any caller remembering to exclude them.
/// </para>
/// </summary>
public class DocumentReadScopeFilter_Tests
{
    private static readonly Guid TypeA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000632");
    private static readonly Guid TypeB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000632");

    [Fact]
    public void Null_scope_is_an_absent_filter_and_keeps_every_row_including_untyped_ones()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadableDocumentTypeIds = null
        }).ToList();

        rows.Count.ShouldBe(4);
    }

    [Fact]
    public void A_scope_keeps_only_its_own_types_rows()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadableDocumentTypeIds = new[] { TypeA }
        }).ToList();

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(d => d.DocumentTypeId == TypeA);
    }

    /// <summary>
    /// The distinction the chain has an explicit branch for. Every other member of the filter treats "no value" as
    /// "do not filter"; here an empty set is a real answer — a caller with entry and no grants — and collapsing it
    /// into "absent filter" would hand that caller the whole layer.
    /// </summary>
    [Fact]
    public void An_empty_scope_is_may_read_nothing_not_an_absent_filter()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadableDocumentTypeIds = Array.Empty<Guid>()
        }).ToList();

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// Untyped rows (unclassified / failed classification / containers) belong to no type, so no grant can name
    /// them: they are out of scope even when every existing type is in it. The same fail-closed rule
    /// <c>DocumentTypeAccessChecker</c> applies to a single document.
    /// </summary>
    [Fact]
    public void Untyped_rows_are_excluded_even_when_every_type_is_in_scope()
    {
        var rows = Query(Documents()).ApplyMetadataFilter(new DocumentMetadataFilter
        {
            ReadableDocumentTypeIds = new[] { TypeA, TypeB }
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
            ReadableDocumentTypeIds = new[] { TypeA }
        }).ToList();

        // The requested type is outside the caller's scope: AND-combined, so nothing comes back rather than the
        // later predicate winning.
        rows.ShouldBeEmpty();
    }

    private static IQueryable<Document> Query(IEnumerable<Document> documents) => documents.AsQueryable();

    private static List<Document> Documents() =>
    [
        NewDocument(TypeA),
        NewDocument(TypeA),
        NewDocument(TypeB),
        NewDocument(documentTypeId: null)
    ];

    private static Document NewDocument(Guid? documentTypeId)
    {
        var document = new Document(
            Guid.NewGuid(),
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

        return document;
    }
}

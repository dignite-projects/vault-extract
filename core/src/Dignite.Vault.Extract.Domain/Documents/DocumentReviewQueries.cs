using System;
using System.Linq.Expressions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// EF-translatable query predicates for the document review axis (#284). The canonical, reusable form of the
/// "requires operator attention" rule, so query sites (the review queue in <c>DocumentAppService.ApplyFilter</c>
/// and the overview needs-review statistic, #333) do not each inline their own copy and drift apart.
/// <para>
/// This is the queryable twin of <see cref="ReviewReasonPolicy.RequiresAttention"/> — a scalar method in
/// Domain.Shared used for in-memory DTO projection. Both encode the same rule
/// (<c>ReviewReasons != None &amp;&amp; ReviewDisposition != Rejected</c>). The scalar lives in Domain.Shared because it
/// cannot reference <see cref="Document"/>; this <see cref="Expression"/> lives in Domain so it can be composed
/// into LINQ queries. Keep the two in sync.
/// </para>
/// </summary>
public static class DocumentReviewQueries
{
    /// <summary>
    /// Documents needing operator attention: any unresolved review reason is present and the operator has not
    /// rejected the document. Rejected documents have already been handled, so they leave the queue.
    /// </summary>
    public static Expression<Func<Document, bool>> RequiresAttention { get; } =
        document => document.ReviewReasons != DocumentReviewReasons.None
                 && document.ReviewDisposition != DocumentReviewDisposition.Rejected;
}

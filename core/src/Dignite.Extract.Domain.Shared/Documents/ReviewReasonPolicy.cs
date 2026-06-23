namespace Dignite.Extract.Documents;

/// <summary>
/// <b>Blocking</b> policy for review reasons: the single declaration point for which
/// <see cref="DocumentReviewReasons"/> values block the Ready gate.
/// <para>
/// <c>[Flags]</c> members cannot carry metadata, so the blocking set is a bitwise-AND-able constant.
/// The Ready gate (<c>DocumentPipelineRunManager.DeriveLifecycleAsync</c>) and future added reasons
/// only need this one place changed. Add a blocking reason by ORing it into <see cref="Blocking"/>;
/// add a non-blocking reason by leaving it out.
/// </para>
/// </summary>
public static class ReviewReasonPolicy
{
    /// <summary>Reasons that block Ready. Any one of them makes the document unavailable to downstream consumers.</summary>
    public const DocumentReviewReasons Blocking =
        DocumentReviewReasons.UnresolvedClassification | DocumentReviewReasons.DuplicateSuspected;

    /// <summary>Whether any blocking reason is present; this is the Ready gate criterion.</summary>
    public static bool HasBlocking(DocumentReviewReasons reasons) => (reasons & Blocking) != DocumentReviewReasons.None;

    /// <summary>
    /// Whether the operator still needs to pay attention to this document. This is the <b>only
    /// criterion</b> for outbound <c>RequiresReview</c> / review queue (#284 review-fix): unresolved
    /// reasons are present <b>and</b> the document is not rejected. <c>RejectReview</c> intentionally
    /// keeps objective reasons because rejection is recoverable, so rejected documents may still carry
    /// reasons but no longer count as needing attention. This avoids detail pages contradicting
    /// themselves as "rejected + pending review" and keeps filter counts honest. The review-queue EF
    /// query uses the equivalent inline predicate
    /// (<c>ReviewReasons != None &amp;&amp; ReviewDisposition != Rejected</c>; see
    /// <c>DocumentAppService.ApplyFilter</c>), so both places share the same source semantics.
    /// </summary>
    public static bool RequiresAttention(DocumentReviewReasons reasons, DocumentReviewDisposition disposition)
        => reasons != DocumentReviewReasons.None && disposition != DocumentReviewDisposition.Rejected;

    /// <summary>
    /// Whether one reason is blocking, used for outbound DTO <c>IsBlocking</c> projection. The server
    /// fills it from policy so clients no longer decide this themselves.
    /// </summary>
    public static bool IsBlocking(DocumentReviewReasons reason) => (reason & Blocking) != DocumentReviewReasons.None;
}

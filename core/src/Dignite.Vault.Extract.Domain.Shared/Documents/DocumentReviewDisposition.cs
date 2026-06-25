namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Disposition stage</b> for manual document review, the operator-action axis. It records only
/// "what did the operator do to this document". Orthogonal to <b>review reasons</b>
/// (<see cref="DocumentReviewReasons"/>, objective unresolved problems): the disposition axis is
/// written only by operator actions (confirm / reject), while the reason axis is written only by
/// pipelines / evaluators, with no overlapping write points.
/// <para>
/// "Whether operator attention is needed" is derived from
/// <c>ReviewReasons != None</c> and this enum not being Rejected; see
/// <see cref="ReviewReasonPolicy"/>. It is <b>not</b> represented as a value in this enum. The old
/// <c>PendingReview</c> moved to <see cref="DocumentReviewReasons.UnresolvedClassification"/> because
/// it is a reason, not a disposition. Member numeric values align with the old
/// <c>DocumentReviewStatus</c> so DB int values stay unchanged:
/// None->NotReviewed(0), Reviewed->Confirmed(20), Rejected(30).
/// </para>
/// </summary>
public enum DocumentReviewDisposition
{
    /// <summary>Operator has not yet acted (default). Whether attention is needed depends on <see cref="DocumentReviewReasons"/>.</summary>
    NotReviewed = 0,

    /// <summary>Operator confirmed the document type (manual classification / reclassification).</summary>
    Confirmed = 20,

    /// <summary>
    /// Operator rejected the document (#237, recoverable: later Reclassify moves it back to
    /// <see cref="Confirmed"/>). Rejection must carry <c>Document.RejectionReason</c>.
    /// </summary>
    Rejected = 30
}

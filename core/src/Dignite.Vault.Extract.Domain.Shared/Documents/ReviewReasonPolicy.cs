namespace Dignite.Vault.Extract.Documents;

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
        DocumentReviewReasons.UnresolvedClassification |
        DocumentReviewReasons.DuplicateSuspected |
        DocumentReviewReasons.FieldExtractionIncomplete |
        DocumentReviewReasons.FieldValidationWarning;

    /// <summary>Whether any blocking reason is present; this is the Ready gate criterion.</summary>
    public static bool HasBlocking(DocumentReviewReasons reasons) => (reasons & Blocking) != DocumentReviewReasons.None;

    /// <summary>
    /// The blocking reasons that <b>close the ownership arm of the edit family</b> (#635): every blocking reason
    /// except <see cref="DocumentReviewReasons.UnresolvedClassification"/>.
    /// <para>
    /// #635 first put "an uploader must not clear a blocking review reason on their own document" on the three
    /// explicit review methods. That does not hold, because the edit family clears the same bits as a side
    /// effect: re-extraction and a retried field-extraction run replace the whole validation-warning set and
    /// recompute the duplicate fingerprint from the new values, <c>UpdateExtractedFieldsAsync</c> recomputes that
    /// fingerprint from corrected values (#651 §6), and <c>ConfirmClassification</c> resets duplicate state and
    /// clears warnings. So the rule has to live where
    /// it can actually hold: while one of these is present, an owner may still <b>read</b> and <b>delete</b>
    /// their document, but modifying it is for someone holding the module-wide permission or the per-type grant.
    /// </para>
    /// <para>
    /// #657 removed the starkest case this set was built for: <c>UpdateExtractedFieldsAsync</c> used to clear
    /// <see cref="DocumentReviewReasons.FieldExtractionIncomplete"/> outright, and an empty field set was enough
    /// to trigger it — releasing a document to Ready with no field values at all. That reason is now cleared only
    /// by the explicit <c>ConfirmFieldEntryAsync</c>, which runs on the Review rule. The side effects listed above
    /// are the ones that remain, and they are why this set is still derived rather than narrowed to them.
    /// </para>
    /// <para>
    /// <b>Derived</b> from <see cref="Blocking"/> rather than listed, so a blocking reason added later is
    /// owner-locking by default and has to be excluded deliberately.
    /// <see cref="DocumentReviewReasons.UnresolvedClassification"/> is the one exclusion: confirming or
    /// reclassifying one's own upload is exactly what an uploader is expected to do, and the target type is
    /// judged separately by the DeclareType rule, which has no ownership arm at all.
    /// </para>
    /// </summary>
    public const DocumentReviewReasons OwnerLocking =
        Blocking & ~DocumentReviewReasons.UnresolvedClassification;

    /// <summary>
    /// Whether this document's review state closes the ownership arm of the edit family — see
    /// <see cref="OwnerLocking"/>. A document blocked only on classification is <b>not</b> locked.
    /// </summary>
    public static bool LocksOwnerEdits(DocumentReviewReasons reasons)
        => (reasons & OwnerLocking) != DocumentReviewReasons.None;

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

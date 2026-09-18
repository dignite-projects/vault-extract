namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// How far a document's own uploader gets on one rule of <see cref="DocumentAccessRule"/>'s table, without any
/// grant (#635 decision 1).
/// <para>
/// Three-valued rather than a bool because the ownership right is not uniform across the table: an uploader must
/// always be able to see and withdraw their own upload, must never be the one who signs off its review, and must
/// not be able to <b>modify</b> it while it is blocked on something a second pair of eyes exists to settle.
/// </para>
/// </summary>
public enum DocumentOwnerArm
{
    /// <summary>
    /// The uploader gets nothing from owning the document. Review (signing off a blocking reason is somebody
    /// else's job), DeclareType (owning a document is not a licence to move it into an ungranted type), Upload
    /// (nothing exists yet to own), and every row with no per-type arm either.
    /// </summary>
    Never = 0,

    /// <summary>
    /// The uploader may always perform it, whatever state the document is in: Read, Delete, Restore. Seeing and
    /// withdrawing one's own upload is the whole point of the ownership axis, and neither clears a review reason.
    /// </summary>
    Always = 1,

    /// <summary>
    /// The uploader may perform it <b>unless</b> the document carries a blocking review reason other than
    /// classification (<see cref="ReviewReasonPolicy.OwnerLocking"/> — see there for why): Edit and Retry.
    /// <para>
    /// Both families clear those bits as a side effect rather than by asking to — an empty field set clears
    /// <c>FieldExtractionIncomplete</c>, re-extraction and a retried field-extraction run replace the whole
    /// validation-warning set and recompute the duplicate fingerprint — so closing only the three explicit
    /// review methods to an owner would leave the same escape open one door along. A document blocked only on
    /// classification stays owner-editable: confirming one's own upload is intended.
    /// </para>
    /// </summary>
    UnlessUnderReview = 2
}

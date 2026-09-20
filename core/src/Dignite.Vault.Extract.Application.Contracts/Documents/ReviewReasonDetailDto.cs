using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Structured detail for one review reason (#284), rendered by the operator UI. <b>Server computes /
/// client only renders</b>: <see cref="IsBlocking"/> is filled by the server from
/// <c>ReviewReasonPolicy</c>, so clients do not re-decide which reasons are blocking.
/// <see cref="MissingFieldNames"/> is populated only for
/// <see cref="DocumentReviewReasons.MissingRequiredFields"/> and contains display names of missing
/// required fields. <see cref="DuplicateCandidates"/> is populated only for
/// <see cref="DocumentReviewReasons.DuplicateSuspected"/> (#411).
/// </summary>
public class ReviewReasonDetailDto
{
    /// <summary>Single reason represented by this detail, not a combination.</summary>
    public DocumentReviewReasons Reason { get; set; }

    /// <summary>Whether this blocks downstream use (Ready gate). Projected by the server from policy and used directly by clients.</summary>
    public bool IsBlocking { get; set; }

    /// <summary>Display names of missing required fields, non-empty only for MissingRequiredFields.</summary>
    public List<string>? MissingFieldNames { get; set; }

    /// <summary>
    /// Other documents suspected to be the same business entity (same layer + type + field fingerprint), non-empty
    /// only for <see cref="DocumentReviewReasons.DuplicateSuspected"/> (#411). Each carries a title / file name +
    /// upload time so the operator can recognize and open it to compare before allowing or discarding. Recomputed on
    /// read and hard-capped.
    /// </summary>
    public List<DuplicateCandidateDto>? DuplicateCandidates { get; set; }

    /// <summary>
    /// How many duplicate candidates exist that this caller may <b>not</b> be shown (#651 §7): the count under an
    /// unrestricted read minus the count in <see cref="DuplicateCandidates"/>. Non-null only for
    /// <see cref="DocumentReviewReasons.DuplicateSuspected"/>, and <c>0</c> whenever the caller's read scope hides
    /// nothing.
    /// <para>
    /// It exists so the client can tell <b>"nothing collides any more"</b> (empty list, zero hidden — the other
    /// copy was deleted, and the operator may simply allow the document) apart from <b>"something collides, but
    /// not for your eyes"</b> (empty list, non-zero hidden). The second is a real trap for an uploader who
    /// reaches documents only through the ownership arm: the pipeline detects layer-wide and blocks them,
    /// the panel narrows to their own documents and shows nothing, and <c>DocumentAccessRule.Review</c>'s
    /// <c>OwnerArm: Never</c> means they cannot clear it either. Without this count the UI can only render an
    /// empty list under a blocking reason, which explains nothing and suggests no action.
    /// </para>
    /// <para>
    /// Both counts are taken under the same <c>MaxDuplicateCandidates</c> cap, so on a widely-shared fingerprint
    /// this is a floor ("at least N others"), not an exact total — which is all the UI needs to say "an
    /// administrator must resolve this".
    /// </para>
    /// </summary>
    public int? HiddenDuplicateCandidateCount { get; set; }

    /// <summary>
    /// Field validation warnings, non-empty only for <see cref="DocumentReviewReasons.FieldValidationWarning"/> (#527).
    /// Each carries the field id / name / display name and the escaped message so the operator can compare the value
    /// against the source and resolve. The warning text lives only here on the REST detail surface — never in field
    /// values, search, CSV/XLSX export, or ETO payloads (#527 §11).
    /// </summary>
    public List<FieldValidationWarningDto>? FieldValidationWarnings { get; set; }
}

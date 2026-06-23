using System;
using System.Collections.Generic;

namespace Dignite.Extract.Documents;

/// <summary>
/// Structured detail for one review reason (#284), rendered by the operator UI. <b>Server computes /
/// client only renders</b>: <see cref="IsBlocking"/> is filled by the server from
/// <c>ReviewReasonPolicy</c>, so clients do not re-decide which reasons are blocking.
/// <see cref="MissingFieldNames"/> is populated only for
/// <see cref="DocumentReviewReasons.MissingRequiredFields"/> and contains display names of missing
/// required fields. <see cref="DuplicateCandidateDocumentIds"/> is populated only for
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
    /// Ids of other documents suspected to be the same business entity (same layer + type + field fingerprint),
    /// non-empty only for <see cref="DocumentReviewReasons.DuplicateSuspected"/> (#411). The operator follows these to
    /// compare candidates side by side before allowing or discarding. Recomputed on read and hard-capped.
    /// </summary>
    public List<Guid>? DuplicateCandidateDocumentIds { get; set; }
}

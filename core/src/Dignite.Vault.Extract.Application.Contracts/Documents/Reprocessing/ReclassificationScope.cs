namespace Dignite.Vault.Extract.Documents.Reprocessing;

/// <summary>
/// Scope for bulk reclassification (#289 scenario one). Humans choose the scope by intent; the system
/// provides no default because classification is "choose one from a competing candidate set", and
/// changing one type description changes the whole classification function, potentially affecting any
/// document's decision.
/// </summary>
public enum ReclassificationScope
{
    /// <summary>
    /// Only documents currently classified as the specified type (local correction: remove those that
    /// no longer fit). Cheapest and most limited blast radius. <b>Blind spot</b>: cannot recover
    /// documents that should belong here but were classified elsewhere; use <see cref="AllDocuments"/>
    /// to include new documents. Requires <c>DocumentTypeId</c>.
    /// </summary>
    OnlyCurrentType = 0,

    /// <summary>
    /// All / cross-type scope, both removing and adding. <b>The only viable scope for new types or for
    /// gathering scattered same-class documents</b>, because a new type has no already-classified
    /// documents. Most expensive and largest blast radius because all documents compete again; guarded
    /// by protecting manual confirmations plus a heavy warning.
    /// </summary>
    AllDocuments = 10,

    /// <summary>Pending review queue: recover documents that previously failed classification and carry the <see cref="DocumentReviewReasons.UnresolvedClassification"/> reason (#284 two-axis model, formerly PendingReview). Small and safe scope.</summary>
    PendingReviewQueue = 20
}

namespace Dignite.Vault.Extract.Documents;

public enum DocumentLifecycleStatus
{
    /// <summary>The file has been stored, and no pipeline has started yet.</summary>
    Uploaded = 10,

    /// <summary>At least one critical pipeline is still running or has not started yet.</summary>
    Processing = 20,

    /// <summary>
    /// Every critical pipeline has run as far as it can, but a <b>blocking</b> review reason
    /// (one of <see cref="ReviewReasonPolicy.Blocking"/>) withholds <see cref="Ready"/> — the document is
    /// waiting on an operator decision, not on the pipeline (#510). Distinct from <see cref="Processing"/>
    /// ("a critical pipeline is still running"), so the operator UI stops showing a spinner for a document that
    /// has actually finished processing and now needs a human. Because it is <b>not</b> <see cref="Ready"/>,
    /// <c>DocumentReadyEto</c> is not fired and the document is not released to downstream consumers — the same
    /// withholding as <see cref="Processing"/>, only with an honest appearance.
    /// </summary>
    PendingReview = 25,

    /// <summary>
    /// All critical pipelines, Parse and Classification, completed successfully.
    /// The document is available to business workflows; non-critical pipelines such as embedding may
    /// still be running.
    /// </summary>
    Ready = 30,

    /// <summary>At least one critical pipeline has finally failed after retries were exhausted.</summary>
    Failed = 99
}

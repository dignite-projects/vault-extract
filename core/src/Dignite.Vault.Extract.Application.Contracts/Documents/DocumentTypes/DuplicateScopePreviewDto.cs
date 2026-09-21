namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// What switching a type's <see cref="DocumentTypeDto.DuplicateScope"/> would do, shown in the type form
/// <b>before</b> the save that triggers it (#651 §5). Saving enqueues <c>DuplicateScopeReconciliationJob</c>
/// automatically — between saving and a hypothetical second "apply" button the system would be stating a verdict
/// it no longer believes — so the decision the admin makes is "do I want this rule", and this is the information
/// they make it with.
/// <para>
/// <b>The two counts are the real diff, not an estimate.</b> They are produced by the same component the
/// reconciliation job applies, run over every page of the type under <see cref="ProspectiveScope"/> and counted
/// instead of written, so they are exactly the documents the job will then change.
/// </para>
/// <para>
/// <b>Both directions re-evaluate every fingerprinted document</b>, so both can flag and both can clear —
/// neither number belongs to one direction. Narrowing (<c>Layer</c> → <c>Uploader</c>) mostly clears false
/// parks, but it can flag too: the stored bit is a snapshot taken at each document's own extraction, and the
/// <i>first</i> upload of a key was written before its twin existed, so it sits unflagged while the twin carries
/// the flag. Re-evaluation gives that first document the verdict it would have today. Widening
/// (<c>Uploader</c> → <c>Layer</c>) mostly flags, and clears for the mirror reason.
/// </para>
/// <para>
/// <b>Any newly flagged document that was Ready returns to review</b>, in either direction, and its
/// <c>DocumentReadyEto</c> has already been consumed downstream. There is no "un-Ready" event; this is existing
/// semantics (re-extracting a Ready document does the same) and at-least-once consumers already tolerate it, but
/// it is the consequence <see cref="WillFlagCount"/> exists to put a number on before the save rather than
/// after it.
/// </para>
/// </summary>
public class DuplicateScopePreviewDto
{
    /// <summary>The type's currently persisted scope.</summary>
    public DuplicateDetectionScope CurrentScope { get; set; }

    /// <summary>The scope the caller is considering, echoed back so a stale response cannot be read as fresh.</summary>
    public DuplicateDetectionScope ProspectiveScope { get; set; }

    /// <summary>
    /// Whether <see cref="ProspectiveScope"/> differs from <see cref="CurrentScope"/>. False means saving
    /// reconciles nothing, because the enqueue is conditioned on an actual change. The counts are still computed
    /// and will both be zero, since re-evaluating under the scope already in force can only agree with the
    /// verdicts the pipeline wrote.
    /// </summary>
    public bool WouldChange { get; set; }

    /// <summary>
    /// Live documents of this type whose blocking <c>DuplicateSuspected</c> reason would be <b>set</b> — they
    /// leave Ready and return to the review queue. <c>DuplicateAllowed</c> documents are excluded, because
    /// reconciliation never touches an operator's decision. Recycle-bin documents are reconciled as well but are
    /// not counted here: they are in nobody's queue.
    /// </summary>
    public long WillFlagCount { get; set; }

    /// <summary>
    /// Live documents of this type whose blocking <c>DuplicateSuspected</c> reason would be <b>cleared</b> —
    /// they leave the review queue and, if nothing else blocks them, become Ready and fire
    /// <c>DocumentReadyEto</c>. Same exclusions as <see cref="WillFlagCount"/>.
    /// </summary>
    public long WillClearCount { get; set; }
}

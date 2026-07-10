using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Segments;

/// <summary>
/// A born-digital constituent slice of a <b>container</b> document (#346): the LLM segmentation pass splits the
/// container's Markdown into per-document slices, and each slice is recorded here as the durable, resumable,
/// idempotent work-queue row before it is spawned into its own derived <see cref="Document"/>.
/// <para>
/// Since #371 this is the <b>single</b> sub-document ledger: the unified detection pass records every standalone
/// span here — a born-digital text constituent (<see cref="Kind"/> = <c>Text</c>) or an embedded-figure OCR span
/// (<see cref="Kind"/> = <c>Figure</c>) — replacing the retired image-path <c>DocumentFigure</c> aggregate. It
/// references its source by id through <see cref="SourceDocumentId"/> (DDD reference-by-id, no navigation property),
/// while the DB keeps an FK + CASCADE so hard-deleting the container also removes its segment rows. There is no soft
/// delete: a segment is working state, not an independently restorable document (the derived document it spawns is
/// the first-class, restorable artifact).
/// </para>
/// <para>
/// <b>The slice text lives on the row</b> (<see cref="SliceText"/>): it seeds the spawned derived document's
/// Markdown directly (no re-extraction, so the exact slice is preserved — the #346 "never regenerate slice text"
/// decision). The derived sub-document carries <b>no <c>FileOrigin</c> at all</b> (#487 reverted #481's required
/// <c>FileOrigin</c> back to nullable; a consumer reaches the source by following <c>OriginDocumentId</c> to the
/// parent's blob), so this row is the only durable home of the child's text.
/// <see cref="SegmentKey"/> is the SHA-256 of the slice text and doubles as the derived document's
/// <c>OriginConstituentKey</c>, giving idempotent routing (unique <c>(SourceDocumentId, SegmentKey)</c>);
/// <see cref="Ordinal"/> is reading-order provenance only. A container's status is sticky (#346), so within one mode
/// a key collision only ever comes from a job retry; the one cross-mode case — a concrete document's embedded-figure
/// row surviving into a later container re-recognition (#355) — is handled by the mode-aware, idempotent re-split in
/// <c>DocumentSegmentationJob</c> (#372), which skips the already-persisted key and continues the ordinal.
/// </para>
/// </summary>
public class DocumentSegment : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>Owning container document id (reference-by-id; DB keeps FK + CASCADE).</summary>
    public virtual Guid SourceDocumentId { get; private set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the slice text. Doubles as the derived document's
    /// <c>OriginConstituentKey</c> and is unique per source
    /// (<c>(SourceDocumentId, SegmentKey)</c>), so a job retry never duplicate-spawns.
    /// </summary>
    public virtual string SegmentKey { get; private set; } = default!;

    /// <summary>
    /// The Markdown slice text. Working state on this aggregate: it seeds the derived document's Markdown directly
    /// (skipping re-extraction so the exact slice is preserved). The derived sub-document has no blob of its own
    /// (#487), so this row is where the child's text originates. For a <see cref="DocumentSegmentKind.Figure"/> span
    /// this is the figure transcription only (<c>ImageOcrMarkup.ExtractBodies</c>, #373); for a
    /// <see cref="DocumentSegmentKind.Text"/> span it is the prose with inline figure markers stripped. It is
    /// <b>not</b> a channel text egress, so it does not conflict with Markdown-first (which governs the
    /// <see cref="Document"/> aggregate's text payload). Stored as nvarchar(max), not indexed.
    /// </summary>
    public virtual string SliceText { get; private set; } = default!;

    /// <summary>0-based reading-order position of this slice within the container. Provenance only.</summary>
    public virtual int Ordinal { get; private set; }

    /// <summary>
    /// Which kind of source span this segment was carved from by the unified detection pass (#371): a born-digital
    /// text constituent (<see cref="DocumentSegmentKind.Text"/>) or an embedded-figure OCR span that is itself a
    /// standalone document (<see cref="DocumentSegmentKind.Figure"/>, restored by #494). Drives both the spawn gate
    /// (a Text span routes only while its source is still a container; a Figure span routes regardless) and
    /// retraction (#364): a container→type reclassify retracts <see cref="DocumentSegmentKind.Text"/> children but
    /// keeps <see cref="DocumentSegmentKind.Figure"/> ones.
    /// </summary>
    public virtual DocumentSegmentKind Kind { get; private set; }

    /// <summary>
    /// The derived <see cref="Document"/> spawned from this slice, or <c>null</c> when not (yet) spawned /
    /// classified as a non-document segment.
    /// </summary>
    public virtual Guid? RoutedDocumentId { get; private set; }

    /// <summary>
    /// Routing progress (#346): <see cref="DocumentSegmentStatus.Pending"/> until the job spawns this slice,
    /// then <see cref="DocumentSegmentStatus.Spawned"/> (see <see cref="RoutedDocumentId"/>). This is the durable,
    /// resumable work-queue marker that lets the job crash and resume from the remaining
    /// <see cref="DocumentSegmentStatus.Pending"/> slices without duplicate-spawning or re-paying the LLM split.
    /// A slice the LLM marks as a non-document span (cover / index / transmittal) is never persisted as a row —
    /// the detection pass skips it rather than recording a terminal "not a document" state.
    /// </summary>
    public virtual DocumentSegmentStatus Status { get; private set; }

    protected DocumentSegment()
    {
    }

    public DocumentSegment(
        Guid id,
        Guid? tenantId,
        Guid sourceDocumentId,
        string segmentKey,
        string sliceText,
        int ordinal,
        DocumentSegmentKind kind,
        DocumentSegmentStatus status = DocumentSegmentStatus.Pending)
        : base(id)
    {
        TenantId = tenantId;
        SourceDocumentId = Check.NotDefaultOrNull<Guid>(sourceDocumentId, nameof(sourceDocumentId));
        SegmentKey = Check.NotNullOrWhiteSpace(segmentKey, nameof(segmentKey), DocumentSegmentConsts.MaxSegmentKeyLength);
        SliceText = Check.NotNullOrWhiteSpace(sliceText, nameof(sliceText));
        Ordinal = ordinal;
        Kind = kind;
        Status = status;
    }

    /// <summary>
    /// Records that the job spawned a derived <see cref="Document"/> from this slice (#346): links the derived
    /// document and moves <see cref="Status"/> to <see cref="DocumentSegmentStatus.Spawned"/>. Idempotent
    /// re-routing is <b>no longer guarded by a Document-side unique index</b> (#481 removed it); this row's
    /// <see cref="Status"/> transition, backed by this aggregate's optimistic-concurrency <c>ConcurrencyStamp</c>,
    /// is now the sole idempotency guard — a sequential retry sees <see cref="DocumentSegmentStatus.Spawned"/> and
    /// aborts before calling this method again, and a concurrent double-spawn's loser trips the concurrency check
    /// when persisting this call's write (see <c>DerivedDocumentSpawner</c>).
    /// </summary>
    public void MarkSpawned(Guid routedDocumentId)
    {
        RoutedDocumentId = Check.NotDefaultOrNull<Guid>(routedDocumentId, nameof(routedDocumentId));
        Status = DocumentSegmentStatus.Spawned;
    }
}

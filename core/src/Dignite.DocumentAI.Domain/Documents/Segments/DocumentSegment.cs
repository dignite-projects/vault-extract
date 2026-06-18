using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.DocumentAI.Documents.Segments;

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
/// <b>The slice text lives on the row</b> (<see cref="SliceText"/>), mirroring <c>DocumentFigure.Transcription</c>:
/// it seeds the spawned derived document's text extraction (no re-extraction, so the exact slice is preserved —
/// the #346 "never regenerate slice text" decision), and the derived document's own <c>FileOrigin</c> blob is
/// written fresh from it at spawn time (derived-owned, so it outlives the container). <see cref="SegmentKey"/> is
/// the SHA-256 of the slice text and doubles as the derived document's <c>FileOrigin.ContentHash</c> /
/// <c>OriginConstituentKey</c>, giving idempotent routing (unique <c>(SourceDocumentId, SegmentKey)</c>);
/// <see cref="Ordinal"/> is reading-order provenance only. Re-running segmentation is barred (container status is
/// sticky, #346), so a key collision only ever comes from a job retry, never a deliberate re-split.
/// </para>
/// </summary>
public class DocumentSegment : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>Owning container document id (reference-by-id; DB keeps FK + CASCADE).</summary>
    public virtual Guid SourceDocumentId { get; private set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the slice text. Doubles as the derived document's
    /// <c>OriginConstituentKey</c> / <c>FileOrigin.ContentHash</c> and is unique per source
    /// (<c>(SourceDocumentId, SegmentKey)</c>), so a job retry never duplicate-spawns.
    /// </summary>
    public virtual string SegmentKey { get; private set; } = default!;

    /// <summary>
    /// The Markdown slice text. Working state on this aggregate: it seeds the derived document's text extraction
    /// (skipping re-extraction so the exact slice is preserved) and is written to the derived document's
    /// <c>FileOrigin</c> blob at spawn time. It is <b>not</b> a channel text egress, so it does not conflict with
    /// Markdown-first (which governs the <see cref="Document"/> aggregate's text payload). Mirrors
    /// <c>DocumentFigure.Transcription</c> (nvarchar(max), not indexed).
    /// </summary>
    public virtual string SliceText { get; private set; } = default!;

    /// <summary>0-based reading-order position of this slice within the container. Provenance only.</summary>
    public virtual int Ordinal { get; private set; }

    /// <summary>
    /// Which kind of source span this segment was carved from by the unified detection pass (#371): a born-digital
    /// text constituent (<see cref="DocumentSegmentKind.Text"/>) or an embedded-figure OCR span
    /// (<see cref="DocumentSegmentKind.Figure"/>). Drives retraction (#364): a container→type reclassify retracts
    /// <see cref="DocumentSegmentKind.Text"/> children but keeps <see cref="DocumentSegmentKind.Figure"/> ones.
    /// </summary>
    public virtual DocumentSegmentKind Kind { get; private set; }

    /// <summary>
    /// 1-based source page of a <see cref="DocumentSegmentKind.Figure"/> span (#371): a lightweight recovery anchor
    /// parsed from the <c>[Image OCR p:N]</c> sentinel, so the original image can be recovered by re-parsing the
    /// source file if ever needed (the crop is not persisted). <c>null</c> for a <see cref="DocumentSegmentKind.Text"/>
    /// span or a page-less source. Provenance only — never identity (#210).
    /// </summary>
    public virtual int? PageNumber { get; private set; }

    /// <summary>
    /// The derived <see cref="Document"/> spawned from this slice, or <c>null</c> when not (yet) spawned /
    /// classified as a non-document segment.
    /// </summary>
    public virtual Guid? RoutedDocumentId { get; private set; }

    /// <summary>
    /// Routing progress (#346): <see cref="DocumentSegmentStatus.Pending"/> until the job spawns this slice,
    /// then <see cref="DocumentSegmentStatus.Spawned"/> (see <see cref="RoutedDocumentId"/>) or
    /// <see cref="DocumentSegmentStatus.NotADocument"/> (the LLM marked the slice a cover / index / transmittal).
    /// This is the durable, resumable work-queue marker that lets the job crash and resume from the remaining
    /// <see cref="DocumentSegmentStatus.Pending"/> slices without duplicate-spawning or re-paying the LLM split.
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
        DocumentSegmentStatus status = DocumentSegmentStatus.Pending,
        int? pageNumber = null)
        : base(id)
    {
        TenantId = tenantId;
        SourceDocumentId = Check.NotDefaultOrNull<Guid>(sourceDocumentId, nameof(sourceDocumentId));
        SegmentKey = Check.NotNullOrWhiteSpace(segmentKey, nameof(segmentKey), DocumentSegmentConsts.MaxSegmentKeyLength);
        SliceText = Check.NotNullOrWhiteSpace(sliceText, nameof(sliceText));
        Ordinal = ordinal;
        Kind = kind;
        Status = status;
        PageNumber = pageNumber;
    }

    /// <summary>
    /// Records that the job spawned a derived <see cref="Document"/> from this slice (#346): links the derived
    /// document and moves <see cref="Status"/> to <see cref="DocumentSegmentStatus.Spawned"/>. Idempotent
    /// re-routing is guarded upstream by the unique <c>(OriginDocumentId, OriginConstituentKey)</c> index on
    /// the derived document.
    /// </summary>
    public void MarkSpawned(Guid routedDocumentId)
    {
        RoutedDocumentId = Check.NotDefaultOrNull<Guid>(routedDocumentId, nameof(routedDocumentId));
        Status = DocumentSegmentStatus.Spawned;
    }
}

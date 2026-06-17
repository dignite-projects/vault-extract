using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.DocumentAI.Documents.Figures;

/// <summary>
/// A candidate embedded figure extracted from a source <see cref="Document"/> (#306, Scenario B): the
/// persisted crop blob + its figure-OCR transcription + minimal provenance. Produced by the
/// text-extraction pipeline (one row per de-duplicated embedded image) and later consumed by
/// sub-document routing, which decides whether the figure is itself a document and, if so, spawns a
/// derived <see cref="Document"/> from <see cref="CropBlobName"/>.
/// <para>
/// <b>Own aggregate root, not a <see cref="Document"/> child collection</b> — same rationale as the #216
/// <c>DocumentPipelineRun</c> split: it is written by background jobs and keeps the source aggregate
/// small (no load/lock contention between a contract and its many candidate figures). It references the
/// source by id through <see cref="SourceDocumentId"/> (DDD reference-by-id, no navigation property),
/// while the DB keeps an FK + CASCADE so hard-deleting the source also removes its candidate rows. There
/// is no soft delete: a candidate figure is working state, not an independently restorable document (the
/// derived document it may spawn is the first-class, restorable artifact).
/// </para>
/// <para>
/// <b>Identity is content, not position.</b> <see cref="ContentHash"/> is the SHA-256 of the figure
/// bytes and doubles as the derived document's <c>FileOrigin.ContentHash</c> / <c>OriginConstituentKey</c>,
/// giving idempotent routing (unique <c>(SourceDocumentId, ContentHash)</c>); bbox is deliberately not
/// persisted as identity because it drifts across provider / re-extraction (#210). <see cref="PageNumber"/>
/// is provenance only.
/// </para>
/// </summary>
public class DocumentFigure : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>Owning source document id (reference-by-id; DB keeps FK + CASCADE).</summary>
    public virtual Guid SourceDocumentId { get; private set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the figure bytes. Doubles as the derived document's
    /// <c>OriginConstituentKey</c> / <c>FileOrigin.ContentHash</c> and is unique per source
    /// (<c>(SourceDocumentId, ContentHash)</c>), so re-extraction / job retry never duplicate-spawn.
    /// </summary>
    public virtual string ContentHash { get; private set; } = default!;

    /// <summary>BlobStore key of the persisted candidate crop (<c>figures/{sourceDocumentId}/{contentHash}</c>).</summary>
    public virtual string CropBlobName { get; private set; } = default!;

    /// <summary>Crop image MIME type, for example <c>image/png</c>; carried so routing can build the derived <c>FileOrigin</c>.</summary>
    public virtual string ContentType { get; private set; } = default!;

    /// <summary>1-based source page the figure was found on, or <c>null</c> for page-less formats. Provenance only.</summary>
    public virtual int? PageNumber { get; private set; }

    /// <summary>
    /// Figure-OCR transcription snapshot — the input the routing gate classifies against the source's
    /// tenant type layer. This is working state on a candidate aggregate, <b>not</b> a channel text
    /// egress, so it does not conflict with Markdown-first (which governs the <see cref="Document"/>
    /// aggregate's text payload). The same text is already inlined into the source <c>Document.Markdown</c>.
    /// </summary>
    public virtual string Transcription { get; private set; } = default!;

    /// <summary>
    /// The derived <see cref="Document"/> spawned from this figure, or <c>null</c> when not (yet) routed.
    /// Written by sub-document routing; until then every candidate is unrouted.
    /// </summary>
    public virtual Guid? RoutedDocumentId { get; private set; }

    /// <summary>
    /// Routing progress (#306): <see cref="DocumentFigureStatus.Pending"/> until routing evaluates this
    /// candidate, then <see cref="DocumentFigureStatus.Spawned"/> (a derived document was created — see
    /// <see cref="RoutedDocumentId"/>) or <see cref="DocumentFigureStatus.NotADocument"/> (the gate rejected it
    /// and its crop blob was deleted). This is the durable, resumable work-queue marker that lets routing crash
    /// and resume from the remaining <see cref="DocumentFigureStatus.Pending"/> candidates without
    /// duplicate-spawning or re-paying the gate classification for already-evaluated figures. Mutated by the
    /// routing job (added with that job).
    /// </summary>
    public virtual DocumentFigureStatus Status { get; private set; }

    protected DocumentFigure()
    {
    }

    public DocumentFigure(
        Guid id,
        Guid? tenantId,
        Guid sourceDocumentId,
        string contentHash,
        string cropBlobName,
        string contentType,
        string transcription,
        int? pageNumber = null)
        : base(id)
    {
        TenantId = tenantId;
        SourceDocumentId = Check.NotDefaultOrNull<Guid>(sourceDocumentId, nameof(sourceDocumentId));
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), DocumentFigureConsts.MaxContentHashLength);
        CropBlobName = Check.NotNullOrWhiteSpace(cropBlobName, nameof(cropBlobName), DocumentFigureConsts.MaxCropBlobNameLength);
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType), DocumentFigureConsts.MaxContentTypeLength);
        Transcription = transcription ?? string.Empty;
        PageNumber = pageNumber;
        Status = DocumentFigureStatus.Pending;
    }

    /// <summary>
    /// Records that sub-document routing spawned a derived <see cref="Document"/> from this candidate (#306):
    /// links the derived document and moves <see cref="Status"/> to <see cref="DocumentFigureStatus.Spawned"/>.
    /// Idempotent re-routing is guarded upstream by the unique <c>(OriginDocumentId, OriginConstituentKey)</c> index
    /// on the derived document.
    /// </summary>
    public void MarkSpawned(Guid routedDocumentId)
    {
        RoutedDocumentId = Check.NotDefaultOrNull<Guid>(routedDocumentId, nameof(routedDocumentId));
        Status = DocumentFigureStatus.Spawned;
    }

    /// <summary>
    /// Records that the routing gate judged this figure not itself a document of any candidate type (#306):
    /// moves <see cref="Status"/> to <see cref="DocumentFigureStatus.NotADocument"/>. The caller deletes the
    /// candidate crop blob separately; the row is kept as an audit + idempotency marker so a re-run does not
    /// re-evaluate it.
    /// </summary>
    public void MarkNotADocument()
    {
        Status = DocumentFigureStatus.NotADocument;
    }
}

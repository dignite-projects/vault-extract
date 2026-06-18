using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents.DocumentTypes;
using Dignite.DocumentAI.Documents.Segments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// Unified sub-document detection job (#371): the one Markdown-borne, LLM-decided pass that folds born-digital
/// container segmentation (#346) and figure routing (#306/#365) into a single decision. Enqueued from the
/// classification complete-phase when the source is a container <b>or</b> a concrete document that embeds a
/// standalone document, it runs <see cref="DocumentSegmentationWorkflow"/> over the source's <b>marked</b> Markdown
/// (the working copy still carrying the <c>[Image OCR]…[End OCR]</c> figure sentinels) and spawns each standalone
/// span as a derived <see cref="Document"/> seeded from its (stripped, clean) slice.
/// <para>
/// <b>One identity model, one spawn sink.</b> Every span — text or figure — is keyed by the SHA-256 of its clean
/// slice text and spawned through the shared <see cref="DerivedDocumentSpawner"/> as a text-only sub-document; a
/// figure span carries only a page recovery anchor (no crop). Because figure and text spans now share the
/// <c>(OriginDocumentId, OriginConstituentKey)</c> identity, an inlined-then-also-sliced figure collapses on the
/// unique index — the cross-path duplication of #356 / #359 is structurally impossible (no cross-path dedup code).
/// </para>
/// <para>
/// <b>Container vs embedded-document mode.</b> A container's spans become sub-documents of any kind and the split
/// must yield ≥2 (else <see cref="DocumentReviewReasons.SegmentationIncomplete"/>); a concrete-typed parent keeps
/// its own content and only its embedded <see cref="DocumentSegmentKind.Figure"/> spans are routed (the parent
/// still extracts normally, so a failed/empty route is a clean no-op, never a review flag). The
/// <see cref="DocumentSegmentKind"/> recorded per row drives the #364 retraction filter.
/// </para>
/// <para>
/// <b>Two phases, both resumable + idempotent</b> (unchanged from #346). Phase A runs the one-shot LLM detection
/// (skipped if rows already exist); per the locked #346 decision the LLM returns only verbatim markers and
/// <see cref="MarkdownSlicer"/> cuts deterministically. Phase B spawns a derived document per still-Pending
/// segment; the unique index is the duplicate-spawn backstop; per-segment faults are isolated and surfaced.
/// </para>
/// <para>
/// <b>UoW discipline</b> (background-jobs.md): the LLM detection, blob reads, and slice-blob writes run outside any
/// UoW; only the segment-row inserts and each derived-document insert + status change + pipeline enqueue run inside
/// short UoWs.
/// </para>
/// </summary>
[BackgroundJobName("DocumentAI.DocumentSegmentation")]
public class DocumentSegmentationJob
    : AsyncBackgroundJob<DocumentSegmentationJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentSegmentationWorkflow _segmentationWorkflow;
    private readonly DerivedDocumentSpawner _derivedDocumentSpawner;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly ICurrentTenant _currentTenant;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly DocumentAIBehaviorOptions _behaviorOptions;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentSegmentationJob(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        IDocumentTypeRepository documentTypeRepository,
        DocumentSegmentationWorkflow segmentationWorkflow,
        DerivedDocumentSpawner derivedDocumentSpawner,
        IBlobContainer<DocumentAIDocumentContainer> blobContainer,
        ICurrentTenant currentTenant,
        IGuidGenerator guidGenerator,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<DocumentAIBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _documentTypeRepository = documentTypeRepository;
        _segmentationWorkflow = segmentationWorkflow;
        _derivedDocumentSpawner = derivedDocumentSpawner;
        _blobContainer = blobContainer;
        _currentTenant = currentTenant;
        _guidGenerator = guidGenerator;
        _unitOfWorkManager = unitOfWorkManager;
        _behaviorOptions = behaviorOptions.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    public override async Task ExecuteAsync(DocumentSegmentationJobArgs args)
    {
        var cancellationToken = _cancellationTokenProvider.Token;

        var context = await LoadAsync(args.SourceDocumentId);
        if (context is null)
        {
            return; // document removed before the pass ran
        }

        // Phase A: detect once. If a prior run already persisted segment rows, skip the LLM (resumable, no re-split).
        if (!context.HasExistingSegments)
        {
            await DetectAndPersistAsync(context, cancellationToken);
        }

        // Phase B: spawn a derived document per still-Pending segment. Re-loaded each run, so a retry processes only
        // the slices not yet spawned. No early return on an empty list — a resume/retry that finds nothing Pending
        // must still run FinalizeSegmentationFlagAsync so a container whose last slice was spawned by a concurrent
        // worker still gets its stale flag cleared.
        var pending = await LoadPendingSegmentsAsync(args.SourceDocumentId);

        var failures = new List<Exception>();
        foreach (var segment in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SpawnWithIsolationAsync(failures, segment, context, cancellationToken);
        }

        var remainingPending = await FinalizeSegmentationFlagAsync(context);

        if (failures.Count > 0 && remainingPending > 0)
        {
            // Real faults this run AND slices still Pending -> surface so ABP reschedules; already-spawned segments
            // are terminal and skipped on retry (LoadPendingSegmentsAsync re-reads only the still-Pending ones).
            throw new AggregateException(
                $"Sub-document detection left {remainingPending} slice(s) of source {args.SourceDocumentId} Pending; the job will be retried.",
                failures);
        }
    }

    /// <summary>Load phase (short UoW + a post-UoW blob read): snapshot the source's tenant/uploader, marked Markdown, container flag, parent context, and whether segment rows already exist.</summary>
    protected virtual async Task<DetectionContext?> LoadAsync(Guid sourceDocumentId)
    {
        Document? source;
        bool hasExistingSegments;
        string? parentTypeCode = null;
        string? parentTypeDisplayName = null;

        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            source = await _documentRepository.FindAsync(sourceDocumentId, includeDetails: false);
            if (source is null)
            {
                Logger.LogInformation(
                    "Document {SourceId} is missing (removed after this job was enqueued); skipping sub-document detection.",
                    sourceDocumentId);
                return null;
            }

            hasExistingSegments =
                await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == sourceDocumentId) is not null;

            // Best-effort parent type for the gate's "standalone vs element-of-parent" grounding (#365). The
            // container path has none (DocumentTypeId null); the embedded-document path has the parent's concrete
            // type — classification finished before enqueueing this pass, so it is reliably set there.
            if (source.DocumentTypeId.HasValue)
            {
                using (_currentTenant.Change(source.TenantId))
                {
                    var type = await _documentTypeRepository.FindAsync(source.DocumentTypeId.Value);
                    parentTypeCode = type?.TypeCode;
                    parentTypeDisplayName = type?.DisplayName;
                }
            }

            await uow.CompleteAsync();
        }

        // Marked Markdown is an external blob read -> outside the UoW. Fall back to the clean Document.Markdown when
        // there is no marked artifact (a non-figure container, or an archive that failed open): the detection then
        // runs on clean text, which simply has no figure spans to recognize.
        var markedMarkdown = await TryReadMarkedMarkdownAsync(sourceDocumentId)
                             ?? source.Markdown
                             ?? string.Empty;

        var detection = new SubDocumentDetectionContext(
            source.IsContainer, source.Title, parentTypeCode, parentTypeDisplayName);

        return new DetectionContext(
            sourceDocumentId,
            source.TenantId,
            source.FileOrigin.UploadedByUserName,
            markedMarkdown,
            source.IsContainer,
            hasExistingSegments,
            detection);
    }

    /// <summary>
    /// Phase A: one LLM detection (external, no UoW) over the marked Markdown -> deterministic slicing -> per-span
    /// kind/clean-text/key/page -> a short UoW that inserts the spawnable segment rows. A container with an
    /// untrusted / &lt;2 / over-cap / unparseable result is flagged <see cref="DocumentReviewReasons.SegmentationIncomplete"/>;
    /// an embedded-document parent degrades the same conditions to a logged no-op (it extracts normally).
    /// </summary>
    protected virtual async Task DetectAndPersistAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.MarkedMarkdown))
        {
            await IncompleteOrSkipAsync(context, "the source has no Markdown to segment");
            return;
        }

        // The whole Markdown is fed (boundaries can be anywhere), so an unbounded source is an unbounded prompt-token
        // cost. Above the cap, degrade to a review signal (container) / a logged skip (embedded-document).
        if (context.MarkedMarkdown.Length > _behaviorOptions.MaxSegmentationMarkdownLength)
        {
            await IncompleteOrSkipAsync(
                context,
                $"the Markdown ({context.MarkedMarkdown.Length} chars) exceeds the segmentation limit of {_behaviorOptions.MaxSegmentationMarkdownLength}");
            return;
        }

        // Detection (external, no UoW): the LLM proposes boundaries; keep the ambient tenant aligned as classification
        // does. A schema-drift / non-JSON structured response is a recoverable bad-output case (mirrors
        // DocumentClassificationBackgroundJob): flag/skip rather than letting the exception fault the job into an
        // endless ABP retry loop that never reaches a terminal state.
        DocumentSegmentationOutcome? outcome = null;
        using (_currentTenant.Change(context.TenantId))
        {
            try
            {
                outcome = await _segmentationWorkflow.RunAsync(context.MarkedMarkdown, context.Detection, cancellationToken);
            }
            catch (Exception ex) when (IsSchemaDeserializationError(ex))
            {
                Logger.LogWarning(ex,
                    "AI sub-document detection response failed JSON deserialization for source {SourceId}; flagging/skipping.",
                    context.SourceDocumentId);
            }
        }

        if (outcome is null)
        {
            await IncompleteOrSkipAsync(context, "the AI sub-document detection response could not be parsed (schema drift)");
            return;
        }

        if (!MarkdownSlicer.TrySlice(context.MarkedMarkdown, outcome.Boundaries, out var markedSlices))
        {
            await IncompleteOrSkipAsync(context, "the LLM split could not be verified against the Markdown");
            return;
        }

        // Build a row for each STANDALONE span only (the parent's own content / covers / element-of-parent figures
        // are never persisted): kind from the marked slice's sentinel, clean text + key from the stripped slice, and
        // the figure page recovery anchor. In an embedded-document source only Figure spans route; in a container any
        // kind does.
        var prepared = new List<PreparedSegment>();
        foreach (var slice in markedSlices)
        {
            var isFigure = ImageOcrMarkup.Contains(slice.Text);
            var cleanText = isFigure ? ImageOcrMarkup.Strip(slice.Text) : slice.Text;
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                continue; // a slice that was only sentinels
            }

            var spawn = slice.IsSubDocument && (context.IsContainer || isFigure);
            if (!spawn)
            {
                continue;
            }

            prepared.Add(new PreparedSegment(
                ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(cleanText)),
                cleanText,
                isFigure ? DocumentSegmentKind.Figure : DocumentSegmentKind.Text,
                isFigure ? ExtractFirstPage(slice.Text) : null));
        }

        // Byte-identical detection among the spawnable slices: same content -> same key -> the unique
        // (SourceDocumentId, SegmentKey) index would reject the second insert anyway. Content alone cannot decide
        // whether two identical slices are an accidental duplicate or two genuinely distinct instances, and the pure
        // content hash is needed as the idempotency identity (a positional salt would diverge from the upload path),
        // so abort rather than silently dropping a real instance.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        if (prepared.Any(p => !seenKeys.Add(p.Key)))
        {
            await IncompleteOrSkipAsync(
                context, "byte-identical duplicate slices detected; manual review required to avoid dropping a document");
            return;
        }

        // A container must be a real bundle (≥2 distinct sub-documents); a lone slice is not a bundle, so let an
        // operator reclassify it instead of spawning a duplicate of the container.
        if (context.IsContainer && prepared.Count < 2)
        {
            await MarkSegmentationIncompleteAsync(context, "fewer than two distinct document slices were identified");
            return;
        }

        // An embedded-document parent with nothing standalone to route is a clean no-op: the parent extracts normally.
        if (!context.IsContainer && prepared.Count == 0)
        {
            Logger.LogInformation(
                "Embedded-document source {SourceId}: no embedded standalone document found; nothing to route.",
                context.SourceDocumentId);
            return;
        }

        // Cap the rows inserted in one UoW (blast-radius bound the option promises).
        if (prepared.Count > _behaviorOptions.MaxSegmentsPerDocument)
        {
            await IncompleteOrSkipAsync(
                context,
                $"the split produced {prepared.Count} sub-documents, over the MaxSegmentsPerDocument limit of {_behaviorOptions.MaxSegmentsPerDocument}");
            return;
        }

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // Concurrency guard: another run may have committed segments between LoadAsync and here. Re-check inside
            // the UoW; if so, drop this split and let Phase B spawn from the committed rows (no double-split).
            if (await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == context.SourceDocumentId) is not null)
            {
                await uow.CompleteAsync();
                return;
            }

            // Ordinal is numbered 0..N over the persisted (spawnable) rows so the unique (SourceDocumentId, Ordinal)
            // index makes two concurrent splits collide on Ordinal 0 — only one split survives; the loser resumes
            // from the winner's rows.
            var ordinal = 0;
            foreach (var p in prepared)
            {
                await _segmentRepository.InsertAsync(new DocumentSegment(
                    _guidGenerator.Create(),
                    context.TenantId,
                    context.SourceDocumentId,
                    p.Key,
                    p.CleanText,
                    ordinal++,
                    p.Kind,
                    DocumentSegmentStatus.Pending,
                    p.PageNumber));
            }

            await uow.CompleteAsync();
        }
    }

    /// <summary>Phase B reload (short UoW): snapshot the still-Pending segments to spawn.</summary>
    protected virtual async Task<List<PendingSegment>> LoadPendingSegmentsAsync(Guid sourceDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var pending = await _segmentRepository.GetListAsync(
            s => s.SourceDocumentId == sourceDocumentId && s.Status == DocumentSegmentStatus.Pending);

        var snapshot = pending
            .OrderBy(s => s.Ordinal)
            .Select(s => new PendingSegment(s.Id, s.SegmentKey, s.SliceText, s.Ordinal))
            .ToList();

        await uow.CompleteAsync();

        return snapshot;
    }

    private async Task SpawnWithIsolationAsync(
        List<Exception> failures, PendingSegment segment, DetectionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await SpawnDerivedDocumentAsync(segment, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex,
                "Spawning segment {SegmentId} of source {SourceId} failed; left Pending for job retry.",
                segment.SegmentId, context.SourceDocumentId);
            failures.Add(ex);
        }
    }

    private async Task SpawnDerivedDocumentAsync(
        PendingSegment segment, DetectionContext context, CancellationToken cancellationToken)
    {
        // External (no UoW): write the clean slice to an independent, derived-document-owned blob so the derived
        // document outlives the source (the source's permanent delete reclaims the source/segment rows, not this blob).
        var sliceBytes = Encoding.UTF8.GetBytes(segment.SliceText);
        var derivedBlobName = _guidGenerator.Create().ToString("N") + ".md";
        using (var saveStream = new MemoryStream(sliceBytes, writable: false))
        {
            await _blobContainer.SaveAsync(derivedBlobName, saveStream, overrideExisting: true, cancellationToken);
        }

        var shortKey = segment.SegmentKey.Length > 8 ? segment.SegmentKey[..8] : segment.SegmentKey;
        var fileOrigin = new FileOrigin(
            blobName: derivedBlobName,
            uploadedByUserName: context.UploadedByUserName,
            contentType: "text/markdown",
            contentHash: segment.SegmentKey,
            fileSize: sliceBytes.LongLength,
            originalFileName: $"segment-{shortKey}.md");

        try
        {
            // Shared complete-phase UoW (#358): insert the derived document, mark this segment Spawned, publish
            // DocumentUploadedEto, and queue text extraction — atomically. The reload guard is kind-aware (#371):
            var spawnedId = await _derivedDocumentSpawner.SpawnAsync<DocumentSegment>(
                context.SourceDocumentId,
                context.TenantId,
                segment.SegmentKey,
                fileOrigin,
                reloadClaimable: async () =>
                {
                    var entity = await _segmentRepository.FindAsync(segment.SegmentId);
                    if (entity is not { Status: DocumentSegmentStatus.Pending })
                    {
                        return null;
                    }

                    return await IsStillSpawnableAsync(context.SourceDocumentId, entity.Kind) ? entity : null;
                },
                markSpawned: async (entity, derivedId) =>
                {
                    entity.MarkSpawned(derivedId);
                    await _segmentRepository.UpdateAsync(entity);
                },
                cancellationToken);

            if (spawnedId is null)
            {
                await TryDeleteBlobAsync(derivedBlobName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryDeleteBlobAsync(derivedBlobName);
            throw;
        }
    }

    /// <summary>
    /// Kind-aware stale guard (#371), run inside the spawn UoW (opens none of its own): a
    /// <see cref="DocumentSegmentKind.Text"/> constituent spawns only while the source is <b>still a container</b>
    /// (its bundle premise) — if an operator reclassified it to a concrete type mid-job, the leftover Text segments
    /// are inert and the already-spawned ones are retracted by #364. A <see cref="DocumentSegmentKind.Figure"/>
    /// (genuinely embedded document) spawns as long as the source exists: figure routing is orthogonal to
    /// container-ness, so a concrete-typed parent legitimately keeps it.
    /// </summary>
    private async Task<bool> IsStillSpawnableAsync(Guid sourceId, DocumentSegmentKind kind)
    {
        var source = await _documentRepository.FindAsync(sourceId, includeDetails: false);
        if (source is null)
        {
            return false;
        }

        return kind == DocumentSegmentKind.Figure || source.IsContainer;
    }

    /// <summary>
    /// Phase-A degradation: a container raises the <see cref="DocumentReviewReasons.SegmentationIncomplete"/> review
    /// signal (it produced no usable sub-documents and must not silently yield zero); an embedded-document parent
    /// just logs and returns (it extracts normally — a failed figure route is not the parent's problem).
    /// </summary>
    private async Task IncompleteOrSkipAsync(DetectionContext context, string reason)
    {
        if (context.IsContainer)
        {
            await MarkSegmentationIncompleteAsync(context, reason);
        }
        else
        {
            Logger.LogInformation(
                "Embedded-document source {SourceId}: {Reason}; skipping figure routing (the parent extracts normally).",
                context.SourceDocumentId, reason);
        }
    }

    /// <summary>Flags the container with the non-blocking <see cref="DocumentReviewReasons.SegmentationIncomplete"/> signal (short UoW).</summary>
    private async Task MarkSegmentationIncompleteAsync(DetectionContext context, string reason)
    {
        Logger.LogWarning(
            "Container {SourceId} segmentation incomplete ({Reason}); flagging for operator review.",
            context.SourceDocumentId, reason);

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // Re-check the document is still a container in THIS UoW. It may have been reclassified to a concrete
            // type during the slow LLM detection; flagging the now-typed document would push the operator's
            // reclassification back into the review queue with no path to clear it (this path persists no rows, so
            // FinalizeSegmentationFlagAsync's count-driven clear never runs).
            var container = await FindActiveContainerAsync(context.SourceDocumentId);
            if (container is null)
            {
                await uow.CompleteAsync();
                return;
            }

            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            await _documentRepository.UpdateAsync(container);
            await uow.CompleteAsync();
        }
    }

    /// <summary>
    /// Sets or clears <see cref="DocumentReviewReasons.SegmentationIncomplete"/> from the DB's remaining-Pending
    /// count (short UoW), and returns that count. The flag is a <b>container-only</b> signal: a concrete-typed
    /// (embedded-document) parent extracts normally and must never carry it, so this only ever touches the flag of a
    /// still-container source. Driving the flag off persisted state (not one run's failures) converges correctly even
    /// when concurrent workers collide on each other's spawned segments.
    /// </summary>
    private async Task<int> FinalizeSegmentationFlagAsync(DetectionContext context)
    {
        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == context.SourceDocumentId);
            if (segments.Count == 0)
            {
                await uow.CompleteAsync();
                return 0;
            }

            var remainingPending = segments.Count(s => s.Status == DocumentSegmentStatus.Pending);

            var source = await _documentRepository.FindAsync(context.SourceDocumentId, includeDetails: false);
            // A removed or now-concrete-typed source: leave its review flag untouched (a concrete parent never
            // carries the container-only SegmentationIncomplete signal; its leftover Pending segments are inert).
            if (source is null || !source.IsContainer)
            {
                await uow.CompleteAsync();
                return remainingPending;
            }

            var hasFlag = (source.ReviewReasons & DocumentReviewReasons.SegmentationIncomplete)
                != DocumentReviewReasons.None;
            var shouldHaveFlag = remainingPending > 0;
            if (hasFlag != shouldHaveFlag)
            {
                source.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: shouldHaveFlag);
                await _documentRepository.UpdateAsync(source);
            }

            await uow.CompleteAsync();
            return remainingPending;
        }
    }

    /// <summary>Loads the source by id, returning it only while it is still an active container; null otherwise (runs in the caller's ambient UoW, opens none).</summary>
    private async Task<Document?> FindActiveContainerAsync(Guid sourceId)
    {
        var source = await _documentRepository.FindAsync(sourceId, includeDetails: false);
        return source is { IsContainer: true } ? source : null;
    }

    private async Task<string?> TryReadMarkedMarkdownAsync(Guid documentId)
    {
        var blobName = DocumentConsts.MarkedMarkdownBlobPrefix + documentId;
        try
        {
            var bytes = await _blobContainer.GetAllBytesOrNullAsync(blobName);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to read marked Markdown blob {BlobName} for source {SourceId}; falling back to Document.Markdown.",
                blobName, documentId);
            return null;
        }
    }

    private async Task TryDeleteBlobAsync(string blobName)
    {
        try
        {
            await _blobContainer.DeleteAsync(blobName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete blob {BlobName} during sub-document detection cleanup.", blobName);
        }
    }

    private static int? ExtractFirstPage(string markedSliceText)
    {
        foreach (var line in markedSliceText.Split('\n'))
        {
            if (ImageOcrMarkup.IsOpenLine(line))
            {
                return ImageOcrMarkup.TryParsePage(line);
            }
        }

        return null;
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    /// <summary>Per-source detection context loaded once up front: provenance, the marked Markdown to detect over, the container flag, whether a prior split exists, and the parent context for the LLM.</summary>
    protected sealed record DetectionContext(
        Guid SourceDocumentId,
        Guid? TenantId,
        string UploadedByUserName,
        string MarkedMarkdown,
        bool IsContainer,
        bool HasExistingSegments,
        SubDocumentDetectionContext Detection);

    /// <summary>A standalone span prepared for persistence: its content key, clean slice text, kind, and figure page anchor.</summary>
    private sealed record PreparedSegment(string Key, string CleanText, DocumentSegmentKind Kind, int? PageNumber);

    /// <summary>Detached snapshot of one still-Pending segment, carried across the per-segment external + UoW phases.</summary>
    protected sealed record PendingSegment(Guid SegmentId, string SegmentKey, string SliceText, int Ordinal);
}

public class DocumentSegmentationJobArgs
{
    /// <summary>The source document whose marked Markdown should be analyzed for standalone sub-documents.</summary>
    public Guid SourceDocumentId { get; set; }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// Born-digital container segmentation (#346): the generalization of #306 figure routing to text bundles. Enqueued
/// from the classification complete-phase when a container is detected, it splits the container's Markdown into its
/// constituent documents and spawns each as a derived <see cref="Document"/> seeded from its slice — reusing the
/// same derived-document sink as figure routing.
/// <para>
/// <b>Two phases, both resumable + idempotent.</b> Phase A runs the one-shot LLM split (skipped if segment rows
/// already exist, so a retry never re-splits and never produces drifting boundaries); per the locked #346 decision
/// the LLM returns only verbatim markers and <see cref="MarkdownSlicer"/> does the deterministic, verifiable
/// cutting. Phase B spawns a derived document per still-<see cref="DocumentSegmentStatus.Pending"/> segment; a crash
/// resumes only the unfinished ones, and the unique <c>(OriginDocumentId, OriginConstituentKey)</c> index on
/// <see cref="Document"/> is the duplicate-spawn backstop. Per-segment faults are isolated (one bad slice does not
/// block the rest) and surfaced (the job rethrows so ABP retries the remaining Pending slices).
/// </para>
/// <para>
/// <b>Failure is never silent.</b> If the split cannot be trusted (untrusted markers, fewer than two document
/// slices, or more than <see cref="DocumentAIBehaviorOptions.MaxSegmentsPerDocument"/>), the container is flagged
/// <see cref="DocumentReviewReasons.SegmentationIncomplete"/> (non-blocking — it stays Ready) so an operator can
/// split / reclassify it instead of it quietly producing zero sub-documents.
/// </para>
/// <para>
/// <b>UoW discipline</b> (background-jobs.md): the LLM split and blob IO run outside any UoW; only the
/// segment-row inserts and each derived-document insert + status change + pipeline enqueue run inside short UoWs.
/// </para>
/// </summary>
[BackgroundJobName("DocumentAI.DocumentSegmentation")]
public class DocumentSegmentationJob
    : AsyncBackgroundJob<DocumentSegmentationJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly DocumentSegmentationWorkflow _segmentationWorkflow;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly ICurrentTenant _currentTenant;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly DocumentAIBehaviorOptions _behaviorOptions;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentSegmentationJob(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        DocumentSegmentationWorkflow segmentationWorkflow,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IBlobContainer<DocumentAIDocumentContainer> blobContainer,
        IDistributedEventBus distributedEventBus,
        ICurrentTenant currentTenant,
        IClock clock,
        IGuidGenerator guidGenerator,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<DocumentAIBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _segmentationWorkflow = segmentationWorkflow;
        _pipelineJobScheduler = pipelineJobScheduler;
        _blobContainer = blobContainer;
        _distributedEventBus = distributedEventBus;
        _currentTenant = currentTenant;
        _clock = clock;
        _guidGenerator = guidGenerator;
        _unitOfWorkManager = unitOfWorkManager;
        _behaviorOptions = behaviorOptions.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    public override async Task ExecuteAsync(DocumentSegmentationJobArgs args)
    {
        var cancellationToken = _cancellationTokenProvider.Token;

        var context = await LoadAsync(args.ContainerDocumentId);
        if (context is null)
        {
            return; // container removed before segmentation ran
        }

        // Phase A: split once. If a prior run already persisted segment rows, skip the LLM (resumable, no re-split).
        if (!context.HasExistingSegments)
        {
            await SplitAndPersistAsync(context, cancellationToken);
        }

        // Phase B: spawn a derived document per still-Pending segment. Re-loaded each run, so a retry processes only
        // the slices not yet spawned. NOTE: no early return on an empty list — a resume/retry that finds nothing
        // Pending must still run FinalizeSegmentationFlagAsync below, so a container whose last slice was spawned by
        // a concurrent worker still gets its stale flag cleared.
        var pending = await LoadPendingSegmentsAsync(args.ContainerDocumentId);

        var failures = new List<Exception>();
        foreach (var segment in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SpawnWithIsolationAsync(failures, segment, context, cancellationToken);
        }

        // #346 fix: decide the container's terminal SegmentationIncomplete flag from the DB's remaining-Pending count,
        // NOT from this run's per-segment failures. Two concurrent workers can each collide on the other's
        // just-spawned segment (the unique (OriginDocumentId, OriginConstituentKey) backstop) and so each end with
        // failures > 0 even though every slice is now Spawned; keying off the actual remaining count means whichever
        // run observes zero remaining clears the flag, so a fully-segmented container never lingers in the review
        // queue, and a genuinely incomplete one stays flagged ("failure is never silent").
        var remainingPending = await FinalizeSegmentationFlagAsync(context);

        if (failures.Count > 0 && remainingPending > 0)
        {
            // Real faults this run AND slices still Pending -> surface so ABP reschedules; already-spawned segments
            // are terminal and skipped on retry (LoadPendingSegmentsAsync re-reads only the still-Pending ones), so
            // retries never duplicate.
            throw new AggregateException(
                $"Segmentation left {remainingPending} slice(s) of container {args.ContainerDocumentId} Pending; the job will be retried.",
                failures);
        }
    }

    /// <summary>Load phase (short UoW): snapshot the container's tenant + uploader + Markdown, and whether segment rows already exist.</summary>
    protected virtual async Task<SegmentationContext?> LoadAsync(Guid containerDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        // A stale job may have been enqueued for a document that was since removed, or reclassified away from a
        // container (see FindActiveContainerAsync); in either case there is nothing to segment.
        var container = await FindActiveContainerAsync(containerDocumentId);
        if (container is null)
        {
            Logger.LogInformation(
                "Document {ContainerId} is missing or no longer a container (removed/reclassified after this job was enqueued); skipping segmentation.",
                containerDocumentId);
            return null;
        }

        var hasExistingSegments =
            await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == containerDocumentId) is not null;

        await uow.CompleteAsync();

        return new SegmentationContext(
            containerDocumentId,
            container.TenantId,
            container.FileOrigin.UploadedByUserName,
            container.Markdown ?? string.Empty,
            hasExistingSegments);
    }

    /// <summary>
    /// Loads the container by id, returning it only while it is still an <b>active container</b>. Returns null when
    /// the document was removed, or was reclassified to a concrete type after this job was enqueued (an operator
    /// correction or a high-confidence re-recognition clears <see cref="Document.IsContainer"/>). A stale job must
    /// never split, spawn, or re-flag a document that is no longer a container — doing so would inject spurious
    /// sub-documents downstream, or push the operator's reclassification back into the review queue. This is the
    /// single guard every phase consults (LoadAsync / CommitSpawnAsync / FinalizeSegmentationFlagAsync /
    /// MarkSegmentationIncompleteAsync); it runs in the caller's ambient UoW and opens none.
    /// </summary>
    private async Task<Document?> FindActiveContainerAsync(Guid containerId)
    {
        var container = await _documentRepository.FindAsync(containerId, includeDetails: false);
        return container is { IsContainer: true } ? container : null;
    }

    /// <summary>
    /// Phase A: one LLM split (external, no UoW) → deterministic slicing → validation → a short UoW that inserts
    /// the segment rows. On any untrusted / out-of-bounds result the container is flagged
    /// <see cref="DocumentReviewReasons.SegmentationIncomplete"/> and no rows are written (so Phase B spawns nothing).
    /// </summary>
    protected virtual async Task SplitAndPersistAsync(SegmentationContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Markdown))
        {
            await MarkSegmentationIncompleteAsync(context, "container has no Markdown to segment");
            return;
        }

        // Segmentation feeds the WHOLE Markdown to the LLM (boundaries can be anywhere; truncating would lose tail
        // documents), so an unbounded container is an unbounded prompt-token cost. Above the cap, degrade to a review
        // signal (a human splits / reclassifies it) instead of paying for an enormous, low-confidence single call.
        if (context.Markdown.Length > _behaviorOptions.MaxSegmentationMarkdownLength)
        {
            await MarkSegmentationIncompleteAsync(
                context,
                $"the container Markdown ({context.Markdown.Length} chars) exceeds the segmentation limit of {_behaviorOptions.MaxSegmentationMarkdownLength}");
            return;
        }

        // Gate (external, no UoW): the LLM proposes boundaries; keep the ambient tenant aligned as classification does.
        // A schema-drift / non-JSON structured response is a recoverable bad-output case (mirrors
        // DocumentClassificationBackgroundJob): flag the container for review rather than letting the exception fault
        // the job into an endless ABP retry loop that never reaches a terminal state.
        DocumentSegmentationOutcome? outcome = null;
        using (_currentTenant.Change(context.TenantId))
        {
            try
            {
                outcome = await _segmentationWorkflow.RunAsync(context.Markdown, cancellationToken);
            }
            catch (Exception ex) when (IsSchemaDeserializationError(ex))
            {
                Logger.LogWarning(ex,
                    "AI segmentation response failed JSON deserialization for container {ContainerId}; flagging for review.",
                    context.ContainerId);
            }
        }

        if (outcome is null)
        {
            await MarkSegmentationIncompleteAsync(context, "the AI segmentation response could not be parsed (schema drift)");
            return;
        }

        if (!MarkdownSlicer.TrySlice(context.Markdown, outcome.Boundaries, out var slices))
        {
            await MarkSegmentationIncompleteAsync(context, "the LLM split could not be verified against the Markdown");
            return;
        }

        // Detect byte-identical slices (same content -> same content hash). Content alone cannot decide whether two
        // identical slices are an accidental duplicate (one real document copied/paged twice) or two genuinely
        // distinct instances — and the pure content hash is needed as the SegmentKey / FileOrigin.ContentHash /
        // OriginConstituentKey idempotency identity (positional salt would diverge from the upload + #306 figure
        // paths). So rather than silently collapsing them (and risking dropping a real document instance), flag the
        // whole container for human review. Byte-identical document slices are rare, so the false-trigger cost is low.
        // NB: a duplicate here ABORTS the whole split (it is not silently collapsed) — `seenKeys` only detects the
        // collision; `keyed` is consumed only when there are zero duplicates.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var keyed = new List<(MarkdownSlice Slice, string Key)>(slices.Count);
        var hasDuplicateSlice = false;
        foreach (var slice in slices)
        {
            var key = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(slice.Text));
            if (seenKeys.Add(key))
            {
                keyed.Add((slice, key));
            }
            else
            {
                hasDuplicateSlice = true;
                Logger.LogWarning(
                    "Container {ContainerId} produced a byte-identical slice at ordinal {Ordinal}; flagging for review (cannot tell an accidental duplicate from a genuine repeated instance).",
                    context.ContainerId, slice.Ordinal);
            }
        }

        if (hasDuplicateSlice)
        {
            await MarkSegmentationIncompleteAsync(
                context, "byte-identical duplicate slices detected; manual review required to avoid dropping a document");
            return;
        }

        var documentSliceCount = keyed.Count(p => p.Slice.IsDocument);
        if (documentSliceCount < 2)
        {
            // Fewer than two DISTINCT document slices means this was not really a multi-document bundle; do not spawn
            // a lone duplicate of the container — let an operator reclassify it to a concrete type.
            await MarkSegmentationIncompleteAsync(context, "fewer than two distinct document slices were identified");
            return;
        }

        // Cap the TOTAL number of slices (document + cover/index), not just the document ones, so a flood of
        // non-document slices cannot insert an unbounded number of rows in one UoW — the blast-radius bound the
        // option promises.
        if (keyed.Count > _behaviorOptions.MaxSegmentsPerDocument)
        {
            await MarkSegmentationIncompleteAsync(
                context,
                $"the split produced {keyed.Count} slices, over the MaxSegmentsPerDocument limit of {_behaviorOptions.MaxSegmentsPerDocument}");
            return;
        }

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // Concurrency guard: another run may have committed segments between LoadAsync and here. Re-check inside
            // the UoW; if so, drop this split and let Phase B spawn from the committed rows (no double-split).
            if (await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == context.ContainerId) is not null)
            {
                await uow.CompleteAsync();
                return;
            }

            foreach (var (slice, key) in keyed)
            {
                await _segmentRepository.InsertAsync(new DocumentSegment(
                    _guidGenerator.Create(),
                    context.TenantId,
                    context.ContainerId,
                    key,
                    slice.Text,
                    slice.Ordinal,
                    // Cover / index / transmittal slices are recorded for audit but never spawned.
                    slice.IsDocument ? DocumentSegmentStatus.Pending : DocumentSegmentStatus.NotADocument));
            }

            await uow.CompleteAsync();
        }
    }

    /// <summary>Phase B reload (short UoW): snapshot the still-Pending segments to spawn.</summary>
    protected virtual async Task<List<PendingSegment>> LoadPendingSegmentsAsync(Guid containerDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var pending = await _segmentRepository.GetListAsync(
            s => s.SourceDocumentId == containerDocumentId && s.Status == DocumentSegmentStatus.Pending);

        var snapshot = pending
            .OrderBy(s => s.Ordinal)
            .Select(s => new PendingSegment(s.Id, s.SegmentKey, s.SliceText, s.Ordinal))
            .ToList();

        await uow.CompleteAsync();

        return snapshot;
    }

    /// <summary>
    /// Runs one segment's spawn with per-segment isolation: a fault is logged and collected (so
    /// <see cref="ExecuteAsync"/> can rethrow and trigger a job retry) instead of aborting the remaining segments.
    /// Cancellation is never collected — it propagates so the job is treated as cancelled, not failed.
    /// </summary>
    private async Task SpawnWithIsolationAsync(
        List<Exception> failures, PendingSegment segment, SegmentationContext context, CancellationToken cancellationToken)
    {
        try
        {
            await SpawnDerivedDocumentAsync(segment, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex,
                "Spawning segment {SegmentId} of container {ContainerId} failed; left Pending for job retry.",
                segment.SegmentId, context.ContainerId);
            failures.Add(ex);
        }
    }

    private async Task SpawnDerivedDocumentAsync(
        PendingSegment segment, SegmentationContext context, CancellationToken cancellationToken)
    {
        // External: write the slice to an independent, derived-document-owned blob so the derived document outlives
        // the container (the container's permanent delete reclaims the container/segment rows, not this blob).
        var sliceBytes = Encoding.UTF8.GetBytes(segment.SliceText);
        var derivedBlobName = _guidGenerator.Create().ToString("N") + ".md";
        using (var saveStream = new MemoryStream(sliceBytes, writable: false))
        {
            await _blobContainer.SaveAsync(derivedBlobName, saveStream, overrideExisting: true, cancellationToken);
        }

        var derivedDocumentId = _guidGenerator.Create();

        try
        {
            await CommitSpawnAsync(segment, context, derivedBlobName, derivedDocumentId, sliceBytes.LongLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The spawn UoW rolled back (a concurrent unique-index collision, or any other fault), so the written
            // blob references no committed document — reclaim it, then rethrow so the per-segment handler records
            // the fault and the job is retried (which writes a fresh blob next time). The one CommitSpawnAsync path
            // that returns without throwing (the segment is already non-Pending) deletes its own orphan blob.
            await TryDeleteBlobAsync(derivedBlobName);
            throw;
        }
    }

    /// <summary>
    /// Complete phase (short UoW): inserts the derived document, marks the segment Spawned, publishes
    /// <c>DocumentUploadedEto</c>, and queues the derived document's pipeline — all atomically. Returns early
    /// (deleting the orphan blob) when the segment is no longer Pending or a concurrent run already spawned it.
    /// </summary>
    private async Task CommitSpawnAsync(
        PendingSegment segment, SegmentationContext context, string derivedBlobName, Guid derivedDocumentId, long fileSize)
    {
        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var entity = await _segmentRepository.FindAsync(segment.SegmentId);
            if (entity is null || entity.Status != DocumentSegmentStatus.Pending)
            {
                // Another run already spawned it (or it was removed). Drop our orphan blob and stop.
                await TryDeleteBlobAsync(derivedBlobName);
                return;
            }

            // #346: re-check the container is still a container in THIS UoW — it may have been reclassified between
            // LoadAsync and now (the per-segment spawns span time). If the marker is gone, do not spawn; the
            // document is being handled as a concrete type.
            if (await FindActiveContainerAsync(context.ContainerId) is null)
            {
                await TryDeleteBlobAsync(derivedBlobName);
                return;
            }

            var shortKey = segment.SegmentKey.Length > 8 ? segment.SegmentKey[..8] : segment.SegmentKey;
            var fileOrigin = new FileOrigin(
                blobName: derivedBlobName,
                uploadedByUserName: context.UploadedByUserName,
                contentType: "text/markdown",
                contentHash: segment.SegmentKey,
                fileSize: fileSize,
                originalFileName: $"segment-{shortKey}.md");

            var derived = Document.CreateDerived(
                derivedDocumentId, context.TenantId, fileOrigin, context.ContainerId, segment.SegmentKey);

            // A concurrent run that already committed this segment's derived document trips the unique
            // (OriginDocumentId, OriginConstituentKey) index here. The failure propagates to
            // SpawnDerivedDocumentAsync, which reclaims this run's orphan blob and rethrows so the job retries; on
            // retry the segment is Spawned and skipped — self-healing, no duplicate.
            await _documentRepository.InsertAsync(derived, autoSave: true);

            entity.MarkSpawned(derivedDocumentId);
            await _segmentRepository.UpdateAsync(entity);

            await _distributedEventBus.PublishAsync(
                new DocumentUploadedEto
                {
                    DocumentId = derived.Id,
                    TenantId = derived.TenantId,
                    EventTime = _clock.Now,
                    FileName = fileOrigin.OriginalFileName,
                    FileSize = fileOrigin.FileSize,
                    ContentType = fileOrigin.ContentType
                });

            // Run the derived document through the full normal pipeline. Its text-extraction job seeds Markdown from
            // this segment's SliceText instead of re-extracting the blob (see DocumentTextExtractionBackgroundJob).
            await _pipelineJobScheduler.QueueAsync(derived, DocumentAIPipelines.TextExtraction);

            await uow.CompleteAsync();
        }
    }

    /// <summary>Flags the container with the non-blocking <see cref="DocumentReviewReasons.SegmentationIncomplete"/> signal (short UoW).</summary>
    private async Task MarkSegmentationIncompleteAsync(SegmentationContext context, string reason)
    {
        Logger.LogWarning(
            "Container {ContainerId} segmentation incomplete ({Reason}); flagging for operator review.",
            context.ContainerId, reason);

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // #346: re-check the container is still a container in THIS UoW. It may have been reclassified to a
            // concrete type during the slow LLM split (the window between LoadAsync and here); flagging the now-typed
            // document would push the operator's reclassification back into the review queue with no path to clear it
            // — this Phase A path persists no segment rows, so FinalizeSegmentationFlagAsync's count-driven clear
            // never runs. Mirrors the guard CommitSpawnAsync / FinalizeSegmentationFlagAsync already apply.
            var container = await FindActiveContainerAsync(context.ContainerId);
            if (container is null)
            {
                return;
            }

            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            await _documentRepository.UpdateAsync(container);
            await uow.CompleteAsync();
        }
    }

    /// <summary>
    /// Sets or clears <see cref="DocumentReviewReasons.SegmentationIncomplete"/> on the container from the DB's
    /// remaining-Pending segment count (short UoW; writes only when the flag must change), and returns that count.
    /// When no segment rows exist at all, Phase A already owns the flag (untrusted / &lt;2 / &gt;max / unparseable
    /// split), so it is left untouched. Driving the flag off the persisted state — not a single run's failures —
    /// makes the flag converge correctly even when concurrent workers collide on each other's spawned segments.
    /// </summary>
    private async Task<int> FinalizeSegmentationFlagAsync(SegmentationContext context)
    {
        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == context.ContainerId);
            if (segments.Count == 0)
            {
                // Nothing persisted -> Phase A already decided the flag (or there was nothing to do); don't override it.
                await uow.CompleteAsync();
                return 0;
            }

            var remainingPending = segments.Count(s => s.Status == DocumentSegmentStatus.Pending);

            // #346: if the document was reclassified to a concrete type mid-job (IsContainer cleared), do NOT touch
            // its review flag — its leftover Pending segments are inert (CommitSpawnAsync skips them) and re-flagging
            // a now-typed document would wrongly push the operator's reclassification back into the review queue.
            var container = await FindActiveContainerAsync(context.ContainerId);
            if (container is null)
            {
                await uow.CompleteAsync();
                return 0;
            }

            var hasFlag = (container.ReviewReasons & DocumentReviewReasons.SegmentationIncomplete)
                != DocumentReviewReasons.None;
            var shouldHaveFlag = remainingPending > 0;
            if (hasFlag != shouldHaveFlag)
            {
                container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: shouldHaveFlag);
                await _documentRepository.UpdateAsync(container);
            }

            await uow.CompleteAsync();
            return remainingPending;
        }
    }

    // Mirrors DocumentClassificationBackgroundJob: a structured-output schema drift surfaces as a JsonException
    // (sometimes wrapped); treat it as a recoverable bad-output case, not a job fault.
    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    private async Task TryDeleteBlobAsync(string blobName)
    {
        try
        {
            await _blobContainer.DeleteAsync(blobName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete blob {BlobName} during segmentation cleanup.", blobName);
        }
    }

    /// <summary>Per-container segmentation context loaded once up front: tenant + uploader provenance, the Markdown to split, and whether a prior split exists.</summary>
    protected sealed record SegmentationContext(
        Guid ContainerId,
        Guid? TenantId,
        string UploadedByUserName,
        string Markdown,
        bool HasExistingSegments);

    /// <summary>Detached snapshot of one still-Pending segment, carried across the per-segment external + UoW phases.</summary>
    protected sealed record PendingSegment(Guid SegmentId, string SegmentKey, string SliceText, int Ordinal);
}

public class DocumentSegmentationJobArgs
{
    /// <summary>The container document whose Markdown should be split into sub-documents.</summary>
    public Guid ContainerDocumentId { get; set; }
}

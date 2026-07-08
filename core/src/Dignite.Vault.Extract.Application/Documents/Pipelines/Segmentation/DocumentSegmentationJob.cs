using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.Segmentation;

/// <summary>
/// Unified sub-document detection job (#371): the one Markdown-borne, LLM-decided pass that folds born-digital
/// container segmentation (#346) and figure routing (#306/#365) into a single decision. Enqueued from the
/// classification complete-phase when the source is a container <b>or</b> a concrete document that embeds a
/// standalone document, it runs <see cref="DocumentSegmentationWorkflow"/> over the source's <c>Document.Markdown</c>
/// (which retains the inline <c>*[Image OCR]*…*[End OCR]*</c> figure provenance markers, #381) and spawns each
/// standalone span as a derived <see cref="Document"/> seeded from its (stripped, clean) slice.
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
/// <b>UoW discipline</b> (background-jobs.md): the LLM detection and blob reads run outside any UoW; only the
/// segment-row inserts and each derived-document insert + status change + pipeline enqueue run inside short UoWs.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DocumentSegmentation")]
public class DocumentSegmentationJob
    : AsyncBackgroundJob<DocumentSegmentationJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentSegmentationWorkflow _segmentationWorkflow;
    private readonly DerivedDocumentSpawner _derivedDocumentSpawner;
    private readonly ICurrentTenant _currentTenant;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly VaultExtractBehaviorOptions _behaviorOptions;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentSegmentationJob(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        IDocumentTypeRepository documentTypeRepository,
        DocumentSegmentationWorkflow segmentationWorkflow,
        DerivedDocumentSpawner derivedDocumentSpawner,
        ICurrentTenant currentTenant,
        IGuidGenerator guidGenerator,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<VaultExtractBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _documentTypeRepository = documentTypeRepository;
        _segmentationWorkflow = segmentationWorkflow;
        _derivedDocumentSpawner = derivedDocumentSpawner;
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

        // Phase A: detect once. Skip the LLM split when the document is already marked segmented (#377) — a precise
        // resume gate set in the same transaction as the segment rows. A concrete→container re-recognition (#355)
        // clears the marker, so a re-recognized container runs its container split exactly once (#372).
        if (!context.AlreadySegmented)
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

    /// <summary>Load phase (short UoW): snapshot the source's tenant, Document.Markdown (with inline figure markers, #381), container flag, parent context, and whether the document is already marked segmented (#377, the precise resume gate).</summary>
    protected virtual async Task<DetectionContext?> LoadAsync(Guid sourceDocumentId)
    {
        Document? source;
        bool alreadySegmented;
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

            // #377: resume on the precise IsSegmented marker (set in the same transaction as the segment rows on a
            // terminal SUCCESS), not on inferring completion from segment-row Kind. A concrete document's embedded-run
            // figure row and a figure-only container's split row are indistinguishable by Kind, so the old heuristic
            // either left a re-recognized container undecomposed (#372) or re-ran the LLM forever on a figure-only
            // container; the marker is exact. MarkAsContainer clears it on a concrete→container re-recognition (#355),
            // so the container split runs exactly once.
            alreadySegmented = source.IsSegmented;

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

        // #381: detect over Document.Markdown, which now retains the *[Image OCR]* figure provenance markers inline
        // (no separate marked artifact anymore). A non-figure source simply has no figure spans to recognize.
        var markdown = source.Markdown ?? string.Empty;

        var detection = new SubDocumentDetectionContext(
            source.IsContainer, source.Title, parentTypeCode, parentTypeDisplayName);

        return new DetectionContext(
            sourceDocumentId,
            source.TenantId,
            markdown,
            source.IsContainer,
            alreadySegmented,
            detection,
            // #478: the source's retained-figure manifest (#477) + uploader snapshot, carried so the spawn phase can
            // point a figure sub-document's FileOrigin at the SHARED retained blob without re-loading the source.
            source.ExtractionMetadata?.Figures,
            source.FileOrigin?.UploadedByUserName);
    }

    /// <summary>
    /// Phase A: one LLM detection (external, no UoW) over Document.Markdown -> deterministic slicing -> per-span
    /// kind/clean-text/key/page -> a short UoW that inserts the spawnable segment rows. A container with an
    /// untrusted / &lt;2 / over-cap / unparseable result is flagged <see cref="DocumentReviewReasons.SegmentationIncomplete"/>;
    /// an embedded-document parent degrades the same conditions to a logged no-op (it extracts normally).
    /// </summary>
    protected virtual async Task DetectAndPersistAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Markdown))
        {
            await IncompleteOrSkipAsync(context, "the source has no Markdown to segment");
            return;
        }

        // The whole Markdown is fed (boundaries can be anywhere), so an unbounded source is an unbounded prompt-token
        // cost. Above the cap, degrade to a review signal (container) / a logged skip (embedded-document).
        if (context.Markdown.Length > _behaviorOptions.MaxSegmentationMarkdownLength)
        {
            await IncompleteOrSkipAsync(
                context,
                $"the Markdown ({context.Markdown.Length} chars) exceeds the segmentation limit of {_behaviorOptions.MaxSegmentationMarkdownLength}");
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
                outcome = await _segmentationWorkflow.RunAsync(context.Markdown, context.Detection, cancellationToken);
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

        if (!MarkdownSlicer.TrySlice(context.Markdown, outcome.Boundaries, out var markedSlices))
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
            // #371 hardening (own /code-review): a span's kind is a STRUCTURAL property of its opening boundary, not
            // of whether a sentinel appears somewhere in its body. A genuine text constituent that embeds an inline
            // figure block (#301 inlines the transcription into the body) must stay Kind=Text — otherwise it would be
            // mislabeled Figure and survive the container→type retraction (which keeps Kind==Figure), a #364-class
            // leak. For the clean child seed: a Figure span yields ONLY its figure body (ExtractBodies — drops any
            // surrounding parent text the LLM folded in by omitting a separate parent-body boundary, #373); a Text
            // span keeps its prose and strips only the inline figure sentinels. Either way the child carries no sentinels.
            var isFigure = slice.IsFigure;
            var cleanText = isFigure ? ImageOcrMarkup.ExtractBodies(slice.Text) : ImageOcrMarkup.Strip(slice.Text);
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
                isFigure ? ExtractFirstPage(slice.Text) : null,
                // #478: the retained image's content hash from the in-span figures/{hash} reference (#477); parsed
                // from the RAW slice (the clean seed above deliberately drops the reference line). Null when
                // retention was off — the spawn then leaves FileOrigin null, exactly the pre-#478 behavior.
                isFigure ? ExtractFigureContentHash(slice.Text) : null));
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

        // Constituents this source already routed in a DIFFERENT-mode pass (a concrete doc's embedded figure routed
        // before a container re-recognition, #372/#377): they count toward the container "≥2 real bundle" test and the
        // cap, and the idempotent insert below skips the ones re-detected this run. Heuristic read (tenant-scoped); the
        // insert UoW re-reads authoritatively.
        HashSet<string> existingKeys;
        using (_currentTenant.Change(context.TenantId))
        {
            existingKeys = (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == context.SourceDocumentId))
                .Select(s => s.SegmentKey)
                .ToHashSet(StringComparer.Ordinal);
        }

        var newCount = prepared.Count(p => !existingKeys.Contains(p.Key));

        // A container must be a real bundle (≥2 distinct sub-documents) — counting constituents already routed in a
        // different mode (#377 edge 1), not just this run's freshly-detected spans, so a surviving figure plus one new
        // text correctly reads as a 2-constituent bundle. A lone slice is not a bundle, so let an operator reclassify
        // it instead of spawning a duplicate of the container.
        if (context.IsContainer && existingKeys.Count + newCount < 2)
        {
            await MarkSegmentationIncompleteAsync(context, "fewer than two distinct document slices were identified");
            return;
        }

        // An embedded-document parent with nothing NEW standalone to route is a clean no-op: the parent extracts
        // normally. Mark it segmented (#377) so a retry / re-enqueue does not re-pay the LLM for the same answer.
        if (!context.IsContainer && newCount == 0)
        {
            Logger.LogInformation(
                "Embedded-document source {SourceId}: no embedded standalone document found; nothing to route.",
                context.SourceDocumentId);
            await MarkDocumentSegmentedAsync(context.SourceDocumentId, context.TenantId);
            return;
        }

        // Cap the document's TOTAL constituents (already-routed cross-mode rows + this run's new ones), consistent
        // with the ≥2 floor above — so a cross-mode lifecycle (a concrete doc routes N figures, then is re-recognized
        // as a container that splits M texts) cannot accumulate past the per-document bound the option promises
        // (#377 review). For a fresh split (no existing rows) this is exactly this run's count, unchanged.
        var totalConstituents = existingKeys.Count + newCount;
        if (totalConstituents > _behaviorOptions.MaxSegmentsPerDocument)
        {
            await IncompleteOrSkipAsync(
                context,
                $"the document would have {totalConstituents} sub-documents, over the MaxSegmentsPerDocument limit of {_behaviorOptions.MaxSegmentsPerDocument}");
            return;
        }

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // Load the source to set the completion marker atomically with the rows, and to re-check it: a concurrent
            // run (or a prior committed split) may have finished between LoadAsync and here. If already segmented, drop
            // this run and let Phase B spawn from the committed rows — no double-split, no divergent re-split (#377).
            var source = await _documentRepository.FindAsync(context.SourceDocumentId, includeDetails: false);
            if (source is null || source.IsSegmented)
            {
                await uow.CompleteAsync();
                return;
            }

            // Idempotent insert (#372): skip a span already persisted by an earlier pass in another mode (e.g. the
            // figure a concrete-embedded run already routed, now re-detected by the container split — identical content
            // hash), numbering Ordinal AFTER the existing rows so the unique (SourceDocumentId, Ordinal) index never
            // collides with them. Two CONCURRENT splits still collide on the Ordinal / SegmentKey unique index or the
            // Document concurrency stamp below — only one wins; the loser's UoW throws and the job resumes.
            var committed = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == context.SourceDocumentId);
            var committedKeys = committed.ConvertAll(s => s.SegmentKey).ToHashSet(StringComparer.Ordinal);
            var ordinal = committed.Count == 0 ? 0 : committed.Max(s => s.Ordinal) + 1;
            foreach (var p in prepared)
            {
                if (!committedKeys.Add(p.Key))
                {
                    continue; // already persisted by an earlier pass — same content, same identity
                }

                await _segmentRepository.InsertAsync(new DocumentSegment(
                    _guidGenerator.Create(),
                    context.TenantId,
                    context.SourceDocumentId,
                    p.Key,
                    p.CleanText,
                    ordinal++,
                    p.Kind,
                    DocumentSegmentStatus.Pending,
                    p.PageNumber,
                    p.FigureContentHash));
            }

            // #377: mark the document segmented in the SAME transaction as the rows — the precise resume gate, so a
            // retry / re-enqueue does not re-pay the LLM and a divergent re-split cannot append spurious rows.
            source.MarkSegmented();
            await _documentRepository.UpdateAsync(source);

            await uow.CompleteAsync();
        }
    }

    /// <summary>Short UoW: mark the document segmented (#377) on a terminal no-op (an embedded source with nothing standalone to route), so a retry / re-enqueue does not re-pay the LLM. Tenant-scoped + idempotent.</summary>
    protected virtual async Task MarkDocumentSegmentedAsync(Guid sourceDocumentId, Guid? tenantId)
    {
        using (_currentTenant.Change(tenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var source = await _documentRepository.FindAsync(sourceDocumentId, includeDetails: false);
            if (source is not null && !source.IsSegmented)
            {
                source.MarkSegmented();
                await _documentRepository.UpdateAsync(source);
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
            .Select(s => new PendingSegment(s.Id, s.SegmentKey, s.Ordinal, s.FigureContentHash, s.PageNumber))
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
        // #478 (revisits #393): a Figure-kind segment whose retained image survives in the source's #477 manifest
        // spawns with a FileOrigin pointing at the SHARED extraction-figures blob (no copy — the image is never
        // stored twice); reclaim is reference-aware on both sides (DocumentAppService.PermanentDeleteAsync). A Text
        // segment / retention-off figure keeps FileOrigin null. Markdown is still seeded from segment.SliceText by
        // DocumentParseBackgroundJob (seed precedence) — the blob is provenance, never re-extracted (no drift).
        var fileOrigin = BuildFigureFileOrigin(segment, context);

        // Shared complete-phase UoW (#358): insert the derived document, mark this segment Spawned, publish
        // DocumentUploadedEto, and queue text extraction — atomically. The reload guard is kind-aware (#371).
        await _derivedDocumentSpawner.SpawnAsync<DocumentSegment>(
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

        // A container-independent kind (Figure) spawns regardless; a container-bound kind (Text) only while the
        // source is still a container. Exhaustive — adding a kind forces IsContainerIndependent to declare its stance.
        return kind.IsContainerIndependent() || source.IsContainer;
    }

    /// <summary>
    /// Phase-A degradation: a container raises the <see cref="DocumentReviewReasons.SegmentationIncomplete"/> review
    /// signal (it produced no usable sub-documents and must not silently yield zero); an embedded-document parent
    /// just logs and returns (it extracts normally — a failed figure route is not the parent's problem).
    /// <para>
    /// #377-review note (accepted trade-off): unlike the success and no-op paths, this degradation deliberately does
    /// <b>not</b> set <c>Document.IsSegmented</c> on the embedded path, so a later re-recognition re-runs the LLM
    /// split. That is intentional — a schema-drift / un-verifiable-boundary outcome is usually a transient LLM
    /// hiccup that a retry can resolve, and the figure's transcription stays inline in the parent's Markdown
    /// meanwhile (no data loss). The cost is a re-paid detection call on each operator-driven re-recognition of a
    /// persistently-failing figure route; it is bounded (the job returns cleanly, so ABP does not auto-retry — only
    /// operator action re-enqueues) and low-severity, accepted in favour of recoverability over cost-suppression.
    /// </para>
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

    /// <summary>The retained image's content hash from the slice's first in-span <c>![figure](figures/{hash}.{ext})</c>
    /// reference line (#477/#478), or <c>null</c> when the slice carries none (retention off / pre-#477 content).</summary>
    private static string? ExtractFigureContentHash(string markedSliceText)
    {
        foreach (var line in markedSliceText.Split('\n'))
        {
            var hash = ImageOcrMarkup.TryParseImageReferenceHash(line);
            if (hash is not null)
            {
                return hash;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the figure sub-document's <see cref="FileOrigin"/> (#478) by resolving the segment's
    /// <see cref="DocumentSegment.FigureContentHash"/> against the source's retained-figure manifest (#477) — the
    /// manifest is authoritative (its own stored blob key / content type / size; the key is never rebuilt from
    /// parsed input), and the blob is <b>shared</b> with the source (never copied). Returns <c>null</c> — leaving
    /// today's no-FileOrigin behavior — for a Text segment, a retention-off figure, a manifest miss, or a source
    /// missing its uploader snapshot.
    /// </summary>
    private FileOrigin? BuildFigureFileOrigin(PendingSegment segment, DetectionContext context)
    {
        if (segment.FigureContentHash is null)
        {
            return null;
        }

        var entry = context.FigureManifest?.FirstOrDefault(
            f => string.Equals(f.ContentHash, segment.FigureContentHash, StringComparison.Ordinal));
        if (entry is null)
        {
            Logger.LogInformation(
                "Figure segment {SegmentId} of source {SourceId} references image {Hash}, but the source has no "
                + "matching retained-figure manifest entry (retention off or archive failed); spawning without FileOrigin.",
                segment.SegmentId, context.SourceDocumentId, segment.FigureContentHash);
            return null;
        }

        if (string.IsNullOrWhiteSpace(context.SourceUploadedByUserName))
        {
            Logger.LogWarning(
                "Source {SourceId} has no uploader snapshot; spawning figure segment {SegmentId} without FileOrigin.",
                context.SourceDocumentId, segment.SegmentId);
            return null;
        }

        var extension = FigureReference.Extension(entry.ContentType);
        var originalFileName = segment.PageNumber is { } page && page > 0
            ? $"figure-p{page}.{extension}"
            : $"figure.{extension}";

        return new FileOrigin(
            entry.BlobName,
            context.SourceUploadedByUserName!,
            entry.ContentType,
            entry.ContentHash,
            entry.SizeBytes,
            originalFileName);
    }

    private static bool IsSchemaDeserializationError(Exception ex)
        => ex is JsonException || ex.GetBaseException() is JsonException;

    /// <summary>Per-source detection context loaded once up front: the source id + tenant, the Document.Markdown to
    /// detect over (with inline figure markers, #381), the container flag, whether a prior split exists, the parent
    /// context for the LLM, and — for the #478 figure FileOrigin — the source's retained-figure manifest (#477) plus
    /// its uploader snapshot.</summary>
    protected sealed record DetectionContext(
        Guid SourceDocumentId,
        Guid? TenantId,
        string Markdown,
        bool IsContainer,
        bool AlreadySegmented,
        SubDocumentDetectionContext Detection,
        IReadOnlyList<FigureManifestEntry>? FigureManifest = null,
        string? SourceUploadedByUserName = null);

    /// <summary>A standalone span prepared for persistence: its content key, clean slice text, kind, figure page
    /// anchor, and the retained image's content hash (#478; null for Text / retention off).</summary>
    private sealed record PreparedSegment(
        string Key, string CleanText, DocumentSegmentKind Kind, int? PageNumber, string? FigureContentHash);

    /// <summary>Detached snapshot of one still-Pending segment, carried across the per-segment external + UoW phases.
    /// Carries no slice text: the spawn path keys off <see cref="SegmentKey"/>, and the derived document's Markdown
    /// is seeded by <c>DocumentParseBackgroundJob</c> reading the segment's <c>SliceText</c> column fresh from the DB.
    /// <see cref="FigureContentHash"/> + <see cref="PageNumber"/> feed the #478 figure FileOrigin resolution.</summary>
    protected sealed record PendingSegment(
        Guid SegmentId, string SegmentKey, int Ordinal, string? FigureContentHash = null, int? PageNumber = null);
}

public class DocumentSegmentationJobArgs
{
    /// <summary>The source document whose Markdown (with inline figure markers, #381) should be analyzed for standalone sub-documents.</summary>
    public Guid SourceDocumentId { get; set; }
}

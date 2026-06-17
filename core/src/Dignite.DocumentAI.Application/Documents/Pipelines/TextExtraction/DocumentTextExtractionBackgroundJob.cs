using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Cabinets;
using Dignite.DocumentAI.Documents.Pipelines.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.TextExtraction;

[BackgroundJobName("DocumentAI.DocumentTextExtraction")]
public class DocumentTextExtractionBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentTextExtractionJobArgs>, ITransientDependency
{
    /// <summary>Provider name recorded for a derived sub-document whose Markdown is seeded from the source figure's transcription (#306), instead of re-OCR'ing the crop.</summary>
    private const string ScenarioBSeedProviderName = "ScenarioB-FigureSeed";

    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    /// <summary>
    /// Document-title generation is single-shot, tool-free, prompt-unique. Reuses the
    /// host-registered <see cref="DocumentAIConsts.TitleGeneratorChatClientKey"/> client
    /// (no FunctionInvocation, no DistributedCache) so traces stay clean and hosts can
    /// optionally point title generation at a smaller / cheaper model.
    /// </summary>
    private readonly IChatClient _titleGeneratorChatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly DocumentAIBehaviorOptions _behaviorOptions;
    // ABP BackgroundJobExecuter pushes the job execution cancellation token into ambient state through
    // ICancellationTokenProvider.Use(...) before calling ExecuteAsync. By default the worker source is the host shutdown token,
    // allowing slow external work to be cancelled.
    private readonly ICancellationTokenProvider _cancellationTokenProvider;
    // #306: Scenario B candidate figures are an independent aggregate (default repository), persisted in
    // this job's Complete phase from the crops written in its External phase.
    private readonly IRepository<DocumentFigure, Guid> _documentFigureRepository;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentTextExtractionBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IBackgroundJobManager backgroundJobManager,
        ITextExtractor textExtractor,
        IBlobContainer<DocumentAIDocumentContainer> blobContainer,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        [FromKeyedServices(DocumentAIConsts.TitleGeneratorChatClientKey)] IChatClient titleGeneratorChatClient,
        IPromptProvider promptProvider,
        IOptions<DocumentAIBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider,
        IRepository<DocumentFigure, Guid> documentFigureRepository,
        IGuidGenerator guidGenerator)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _pipelineJobScheduler = pipelineJobScheduler;
        _backgroundJobManager = backgroundJobManager;
        _textExtractor = textExtractor;
        _blobContainer = blobContainer;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _titleGeneratorChatClient = titleGeneratorChatClient;
        _promptProvider = promptProvider;
        _behaviorOptions = behaviorOptions.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
        _documentFigureRepository = documentFigureRepository;
        _guidGenerator = guidGenerator;
    }

    public override async Task ExecuteAsync(DocumentTextExtractionJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        // Hoisted so a Complete-phase failure can reclaim the crop blobs already written in the External phase:
        // they are written before the DocumentFigure rows commit, so a rollback would otherwise orphan them.
        IReadOnlyList<FigureCandidate> figureCandidates = Array.Empty<FigureCandidate>();
        try
        {
            TextExtractionResult result;
            if (workItem.SeedMarkdown != null)
            {
                // #306 derived sub-document: seed Markdown from the source figure's transcription instead of
                // re-OCR'ing the crop. The crop is the exact bytes the figure OCR already transcribed via the
                // same IOcrProvider, so re-OCR would reproduce the same text — seeding is equivalent and saves
                // one OCR call. A crop is a single image, so no embedded figures are surfaced and no recursive
                // sub-document routing occurs.
                result = new TextExtractionResult
                {
                    Markdown = workItem.SeedMarkdown,
                    UsedOcr = false,
                    ProviderName = ScenarioBSeedProviderName,
                    IsComplete = true
                };
            }
            else
            {
                // The caller owns the blob stream. With the FileSystem provider this is a FileStream holding an OS file handle,
                // so it must be disposed, consistent with DocumentAppService.GetBlobAsync disposeStream:true.
                // Use await using so it is released when this try block (External phase) ends; CompleteRunAsync no longer needs it.
                await using var blobStream = await _blobContainer.GetAsync(workItem.BlobName);
                var ctx = new TextExtractionContext
                {
                    ContentType = workItem.ContentType,
                    FileExtension = Path.GetExtension(workItem.OriginalFileName ?? string.Empty),
                    LanguageHints = { "ja", "en" }
                };

                result = await _textExtractor.ExtractAsync(blobStream, ctx, _cancellationTokenProvider.Token);
            }

            var title = await TryGenerateTitleAsync(result.Markdown, _cancellationTokenProvider.Token)
                ?? MarkdownTitleExtractor.ExtractTitle(result.Markdown)
                ?? FallbackTitleFromFileName(workItem.OriginalFileName);

            // External phase (no UoW): archive native payload into blob storage + assemble Domain provenance metadata.
            // Archiving fails open: over limit / write failure / disabled archive affects only the manifest, not text extraction completion below.
            var extractionMetadata = await ArchiveNativePayloadAndBuildMetadataAsync(args.DocumentId, result);

            // External phase (no UoW): persist each de-duplicated embedded figure crop (#306) to blob storage
            // as a Scenario B routing candidate. Fails open per figure (auxiliary work must not break the main
            // Markdown pipeline); the Complete phase below turns the returned descriptors into DocumentFigure rows.
            figureCandidates = await PersistFigureCropsAsync(args.DocumentId, result, _cancellationTokenProvider.Token);

            // The decoded crop bytes are dead once the candidates (which carry only hash / blobName / contentType /
            // transcription) are built; drop them so the whole set is not pinned in memory through the title LLM
            // call's result reference and the Complete-phase reload + commit. CompleteRunAsync never reads Figures.
            result.Figures = null;

            await CompleteRunAsync(
                args.DocumentId, workItem.RunId, result, title, extractionMetadata, figureCandidates);
        }
        catch (Exception ex)
        {
            await FailRunAsync(args.DocumentId, workItem.RunId, ex.Message, DocumentAIPipelines.TextExtraction);

            // The Complete-phase DocumentFigure inserts are atomic with the Markdown commit, so a throw means no
            // row references the crops written in the External phase — reclaim them best-effort so a failed (and
            // possibly never-retried) run does not leak orphan blobs. A later retry re-writes the same
            // content-hash keys, so deleting here is safe. Cleanup failures must not mask the original error.
            await TryDeleteFigureCropsAsync(args.DocumentId, figureCandidates);

            throw;
        }
    }

    private async Task<TextExtractionWorkItem> BeginRunAsync(DocumentTextExtractionJobArgs args)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var document = await DocumentRepository.GetAsync(args.DocumentId, includeDetails: false);
        var run = await PipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, DocumentAIPipelines.TextExtraction);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        // #306: a derived sub-document (OriginDocumentId set) seeds its Markdown from the source figure's
        // transcription rather than re-OCR'ing the crop. Load it inside this UoW; null (not derived, or the
        // candidate is missing/empty) falls back to the normal OCR path in ExecuteAsync.
        string? seedMarkdown = null;
        if (document.OriginDocumentId.HasValue && !string.IsNullOrEmpty(document.OriginConstituentKey))
        {
            var figure = await _documentFigureRepository.FirstOrDefaultAsync(
                f => f.SourceDocumentId == document.OriginDocumentId.Value && f.ContentHash == document.OriginConstituentKey);
            var transcription = figure?.Transcription;
            seedMarkdown = string.IsNullOrEmpty(transcription) ? null : transcription;
        }

        await uow.CompleteAsync();

        return new TextExtractionWorkItem(
            run.Id,
            document.FileOrigin.BlobName,
            document.FileOrigin.ContentType,
            document.FileOrigin.OriginalFileName,
            seedMarkdown);
    }

    private async Task CompleteRunAsync(
        Guid documentId,
        Guid runId,
        TextExtractionResult result,
        string? title,
        DocumentTextExtractionMetadata extractionMetadata,
        IReadOnlyList<FigureCandidate> figureCandidates)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var (document, run) = await LoadDocumentAndRunAsync(
            documentId, runId, DocumentAIPipelines.TextExtraction);

        await PipelineRunManager.CompleteTextExtractionAsync(
            document, run, result.Markdown, title,
            language: result.DetectedLanguage,
            extractionMetadata: extractionMetadata);

        // #306: persist Scenario B candidate figures in the SAME UoW as text-extraction completion, so the
        // figure rows and the Markdown commit atomically. The Markdown write-once invariant makes this success
        // path run at most once per document (a retry after a failed commit re-runs cleanly; a retry after a
        // committed success is rejected by SetMarkdown before reaching here), so candidates insert exactly
        // once. Intra-batch de-duplication by ContentHash already happened during crop persistence; the unique
        // (SourceDocumentId, ContentHash) index is the final guard. RoutedDocumentId stays null until routing.
        foreach (var candidate in figureCandidates)
        {
            await _documentFigureRepository.InsertAsync(new DocumentFigure(
                _guidGenerator.Create(),
                document.TenantId,
                document.Id,
                candidate.ContentHash,
                candidate.CropBlobName,
                candidate.ContentType,
                candidate.Transcription,
                candidate.PageNumber));
        }

        // #306: hand the persisted candidates to sub-document routing (a separate, independently-retried job).
        // Enqueued in this same UoW so the candidates and the routing job commit atomically; no candidates -> no
        // routing job. A derived sub-document is itself seeded (no figures), so it never re-enqueues routing.
        if (figureCandidates.Count > 0)
        {
            await _backgroundJobManager.EnqueueAsync(
                new DocumentFigureRoutingJobArgs { SourceDocumentId = document.Id });
        }

        // Publish OCRCompletedEto with a thin payload; downstream consumers pull Markdown back through REST.
        await _distributedEventBus.PublishAsync(
            new OCRCompletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now,
                UsedOcr = result.UsedOcr,
                FigureOcrCount = result.FigureOcrCount
            });

        // Advance classification as soon as text extraction completes. OCR has no quality gate:
        // #196 found average OCR confidence does not predict real quality. Quality issues are handled later through classification review
        // plus operator rerun / re-upload.
        await _pipelineJobScheduler.QueueAsync(document, DocumentAIPipelines.Classification);

        // #265: after text extraction succeeds and Markdown is ready, fan out the "AI fallback cabinet selection when empty" job.
        // It is an independent sibling orthogonal to the content pipeline, not a PipelineRun phase. Do <b>not</b> read document.CabinetId here:
        // the text extraction job must not touch cabinet state (#194 guardrail). Manual / race gating is handled entirely inside
        // DocumentCabinetSuggestionBackgroundJob.
        // "One-shot" behavior (#265 guardrail 3) is naturally guaranteed by the Markdown write-once invariant:
        // CompleteTextExtractionAsync -> SetMarkdown throws MarkdownIsImmutable when Markdown already exists -> FailRun.
        // Therefore this success path can be hit <b>at most once</b> per document.
        // Retry is allowed only for Failed runs, with the first success happening here, and rerecognize re-enqueues only classification
        // without rerunning text extraction, so neither duplicates the fan-out.
        // Do not gate by AttemptNumber==1: that would miss the first-success case where the first attempt failed and retry succeeded
        // with successful run AttemptNumber > 1.
        await _backgroundJobManager.EnqueueAsync(
            new DocumentCabinetSuggestionJobArgs { DocumentId = document.Id });

        await uow.CompleteAsync();
    }

    /// <summary>
    /// External phase (no UoW): archives the winning provider's native payload into blob storage using a stable per-document key
    /// that is overwritten by re-extraction, and assembles the Domain typed metadata value object (#210).
    /// <para>
    /// <b>Archiving fails open</b>: no payload / over limit / blob write failure -> log warning and set manifest null.
    /// <see cref="DocumentTextExtractionMetadata"/> with provider name <b>still returns normally</b>, and text extraction continues successfully.
    /// Raw spatial signals such as bbox stay in blob storage; the DB stores only the manifest.
    /// </para>
    /// </summary>
    protected virtual async Task<DocumentTextExtractionMetadata> ArchiveNativePayloadAndBuildMetadataAsync(
        Guid documentId,
        TextExtractionResult result)
    {
        var manifest = await TryArchiveNativePayloadAsync(documentId, result.NativePayload);
        return new DocumentTextExtractionMetadata(
            result.ProviderName, manifest, result.IsComplete, result.IncompleteReason);
    }

    /// <summary>
    /// External phase (no UoW): persists each de-duplicated embedded figure crop (#306) to blob storage as a
    /// Scenario B routing candidate, keyed by content hash (<c>figures/{documentId}/{contentHash}</c>), and
    /// returns the descriptors the Complete phase turns into <see cref="DocumentFigure"/> rows. The content
    /// hash doubles as the derived document's <c>OriginConstituentKey</c> / <c>FileOrigin.ContentHash</c>, tying
    /// storage and routing idempotency together.
    /// <para>
    /// <b>Fails open per figure</b>: a crop is an auxiliary routing input, so empty bytes or a blob-write
    /// failure skips only that candidate (logged) — text extraction still succeeds and the figure's
    /// transcription is already inlined into the Markdown.
    /// </para>
    /// </summary>
    protected virtual async Task<IReadOnlyList<FigureCandidate>> PersistFigureCropsAsync(
        Guid documentId, TextExtractionResult result, CancellationToken cancellationToken = default)
    {
        if (result.Figures is not { Count: > 0 } figures)
        {
            return Array.Empty<FigureCandidate>();
        }

        var candidates = new List<FigureCandidate>(figures.Count);
        var seen = new HashSet<string>();
        foreach (var figure in figures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (figure.Content is not { Length: > 0 })
            {
                continue;
            }

            // Fail-open at the source: a Figure with a blank ContentType cannot build a valid DocumentFigure
            // / derived FileOrigin (the entity ctor's Check.NotNullOrWhiteSpace would throw and break the main
            // Markdown pipeline). Skip it here — the figure's transcription is already inlined into Markdown.
            // Today's PdfExtractor always supplies a non-empty image/* type; this guards any other producer.
            if (string.IsNullOrWhiteSpace(figure.ContentType))
            {
                Logger.LogWarning(
                    "Embedded figure for document {DocumentId} has a blank ContentType; skipping this Scenario B candidate (text extraction still succeeds).",
                    documentId);
                continue;
            }

            var contentHash = ContentHasher.Sha256Hex(figure.Content);
            // De-dupe identical embedded images within one document: same bytes -> same hash -> one crop blob
            // + one candidate row (the unique (SourceDocumentId, ContentHash) index is the final guard).
            if (!seen.Add(contentHash))
            {
                continue;
            }

            var blobName = $"figures/{documentId}/{contentHash}";
            try
            {
                using var stream = new MemoryStream(figure.Content, writable: false);
                await _blobContainer.SaveAsync(blobName, stream, overrideExisting: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex,
                    "Failed to persist figure crop {BlobName} for document {DocumentId}; skipping this Scenario B candidate (text extraction still succeeds).",
                    blobName, documentId);
                continue;
            }

            candidates.Add(new FigureCandidate(
                contentHash, blobName, figure.ContentType, figure.PageNumber, figure.Transcription));
        }

        return candidates;
    }

    /// <summary>
    /// Best-effort cleanup of figure crop blobs written in the External phase when the Complete phase failed
    /// (#306). The Complete-phase <see cref="DocumentFigure"/> inserts are atomic with the Markdown commit, so a
    /// failure means no committed row references these crops; without this they orphan, because permanent delete
    /// only reclaims crops via committed rows. Failures here are swallowed so cleanup never masks the original error.
    /// </summary>
    private async Task TryDeleteFigureCropsAsync(Guid documentId, IReadOnlyList<FigureCandidate> figureCandidates)
    {
        foreach (var candidate in figureCandidates)
        {
            try
            {
                await _blobContainer.DeleteAsync(candidate.CropBlobName);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "Failed to clean up orphaned figure crop {BlobName} for document {DocumentId} after a failed text-extraction completion.",
                    candidate.CropBlobName, documentId);
            }
        }
    }

    private async Task<NativePayloadManifest?> TryArchiveNativePayloadAsync(Guid documentId, NativePayload? payload)
    {
        if (payload is null || payload.Content is not { Length: > 0 } content)
        {
            return null;
        }

        // Missing ContentType / SchemaName would make the manifest constructor throw (Check.NotNullOrWhiteSpace), but the NativePayload contract
        // itself does not require non-empty values because future rich Markdown / third-party providers may partially fill it.
        // Fail open before writing the blob: auxiliary audit blobs must never break the main Markdown pipeline or leave unregistered orphan blobs.
        if (string.IsNullOrWhiteSpace(payload.ContentType) || string.IsNullOrWhiteSpace(payload.SchemaName))
        {
            Logger.LogWarning(
                "Native extraction payload for document {DocumentId} has {Bytes} bytes but blank ContentType/SchemaName; "
                + "skipping archive (text extraction still succeeds).",
                documentId, content.Length);
            return null;
        }

        if (content.LongLength > DocumentConsts.MaxNativePayloadArchiveBytes)
        {
            Logger.LogWarning(
                "Native extraction payload for document {DocumentId} is {Size} bytes, exceeding the archive limit "
                + "{Limit}; skipping archive (text extraction still succeeds).",
                documentId, content.LongLength, DocumentConsts.MaxNativePayloadArchiveBytes);
            return null;
        }

        // Stable per-document key: re-extraction overwrites it, one archive blob per document, avoiding orphans.
        var blobName = $"extraction-native/{documentId}";
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            await _blobContainer.SaveAsync(blobName, stream, overrideExisting: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to archive native extraction payload for document {DocumentId} to blob {BlobName}; "
                + "continuing without archive (text extraction still succeeds).",
                documentId, blobName);
            return null;
        }

        var sha256 = ContentHasher.Sha256Hex(content);
        return new NativePayloadManifest(blobName, payload.ContentType, content.LongLength, sha256, payload.SchemaName);
    }

    private async Task<string?> TryGenerateTitleAsync(string markdown, CancellationToken cancellationToken = default)
    {
        try
        {
            var truncated = markdown.Length > _behaviorOptions.MaxTitleGenerationMarkdownLength
                ? markdown[.._behaviorOptions.MaxTitleGenerationMarkdownLength]
                : markdown;

            // Title policy: follow document language, built into the prompt, and do not consume DefaultLanguage; hence no-arg call.
            var template = _promptProvider.GetTitleGenerationPrompt();
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, template.SystemInstructions + "\n\n" + PromptBoundary.BoundaryRule),
                new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
            };

            var response = await _titleGeneratorChatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var title = response.Text?.Trim();

            if (string.IsNullOrWhiteSpace(title))
                return null;

            return title.Length <= DocumentConsts.MaxTitleLength
                ? title
                : title[..DocumentConsts.MaxTitleLength];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "AI title generation failed; falling back to rule-based extractor.");
            return null;
        }
    }

    /// <summary>
    /// Deterministic fallback when Markdown title extraction fails: use the original file name without extension.
    /// If still empty, for example when FileOrigin.OriginalFileName is null in an extreme case, return null and let the UI keep using
    /// the existing file name / blob name display.
    /// </summary>
    private static string? FallbackTitleFromFileName(string? originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            return null;
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(withoutExtension))
        {
            return null;
        }

        var trimmed = withoutExtension.Trim();
        return trimmed.Length <= DocumentConsts.MaxTitleLength
            ? trimmed
            : trimmed[..DocumentConsts.MaxTitleLength];
    }

    private sealed record TextExtractionWorkItem(
        Guid RunId,
        string BlobName,
        string ContentType,
        string? OriginalFileName,
        string? SeedMarkdown);

    /// <summary>
    /// Descriptor of a persisted candidate figure crop (#306). <c>protected</c> because it appears in the
    /// signature of the overridable <see cref="PersistFigureCropsAsync"/>; turned into a
    /// <see cref="DocumentFigure"/> row in the Complete phase.
    /// </summary>
    protected sealed record FigureCandidate(
        string ContentHash, string CropBlobName, string ContentType, int? PageNumber, string Transcription);
}

public class DocumentTextExtractionJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }
}

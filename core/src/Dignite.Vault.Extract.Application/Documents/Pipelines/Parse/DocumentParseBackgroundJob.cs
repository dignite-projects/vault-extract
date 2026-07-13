using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.Parse;

[BackgroundJobName("VaultExtract.DocumentParse")]
public class DocumentParseBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentParseJobArgs>, ITransientDependency
{
    /// <summary>Provider name recorded for a derived sub-document whose Markdown is seeded from a source <see cref="DocumentSegment"/> slice (#346/#371) — a text constituent or a figure span's transcription — instead of re-extracting it.</summary>
    private const string ScenarioBSegmentSeedProviderName = "ScenarioB-SegmentSeed";

    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    /// <summary>
    /// Document-title generation is single-shot, tool-free, prompt-unique. Reuses the
    /// host-registered <see cref="VaultExtractConsts.TitleGeneratorChatClientKey"/> client
    /// (no FunctionInvocation, no DistributedCache) so traces stay clean and hosts can
    /// optionally point title generation at a smaller / cheaper model.
    /// </summary>
    private readonly IChatClient _titleGeneratorChatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly VaultExtractBehaviorOptions _behaviorOptions;
    // ABP BackgroundJobExecuter pushes the job execution cancellation token into ambient state through
    // ICancellationTokenProvider.Use(...) before calling ExecuteAsync. By default the worker source is the host shutdown token,
    // allowing slow external work to be cancelled.
    private readonly ICancellationTokenProvider _cancellationTokenProvider;
    // #346/#371: born-digital container slices / figure spans; looked up in the Begin phase to seed a derived sub-document's Markdown.
    private readonly IRepository<DocumentSegment, Guid> _documentSegmentRepository;
    private readonly ICurrentTenant _currentTenant;

    public DocumentParseBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IBackgroundJobManager backgroundJobManager,
        ITextExtractor textExtractor,
        IBlobContainer<VaultExtractDocumentContainer> blobContainer,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        [FromKeyedServices(VaultExtractConsts.TitleGeneratorChatClientKey)] IChatClient titleGeneratorChatClient,
        IPromptProvider promptProvider,
        IOptions<VaultExtractBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider,
        IRepository<DocumentSegment, Guid> documentSegmentRepository,
        ICurrentTenant currentTenant)
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
        _documentSegmentRepository = documentSegmentRepository;
        _currentTenant = currentTenant;
    }

    public override async Task ExecuteAsync(DocumentParseJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            await ExecuteInTenantAsync(args);
        }
    }

    private async Task ExecuteInTenantAsync(DocumentParseJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        try
        {
            TextExtractionResult result;
            if (workItem.SeedMarkdown != null)
            {
                // Derived sub-document (#371): seed Markdown from the source DocumentSegment slice instead of
                // re-extracting it. The slice is already (clean) Markdown — a born-digital text constituent or a
                // figure span's transcription — so seeding preserves the exact slice text (no drift) and surfaces no
                // embedded figures, so no recursive sub-document detection occurs.
                result = new TextExtractionResult
                {
                    Markdown = workItem.SeedMarkdown,
                    UsedOcr = false,
                    ProviderName = workItem.SeedProviderName ?? ScenarioBSegmentSeedProviderName,
                    IsComplete = true
                };
            }
            else
            {
                // Sub-documents derived from segment SliceText always have SeedMarkdown set; a null BlobName here
                // indicates a configuration or data inconsistency — fail fast rather than NPE on GetAsync.
                if (workItem.BlobName is null)
                    throw new InvalidOperationException(
                        $"Document has no source blob and no seed Markdown (run {workItem.RunId}).");

                // The caller owns the blob stream. With the FileSystem provider this is a FileStream holding an OS file handle,
                // so it must be disposed, consistent with DocumentAppService.GetBlobAsync disposeStream:true.
                // Use await using so it is released when this try block (External phase) ends; CompleteRunAsync no longer needs it.
                await using var blobStream = await _blobContainer.GetAsync(workItem.BlobName);
                var ctx = new TextExtractionContext
                {
                    ContentType = workItem.ContentType!,
                    FileExtension = Path.GetExtension(workItem.OriginalFileName ?? string.Empty)
                    // No language hints set: OCR language is provider-specific (#441). VisionLlm / Azure DI
                    // auto-detect; PaddleOcr falls back to its own PaddleOcr:Languages config.
                };

                result = await _textExtractor.ExtractAsync(blobStream, ctx, _cancellationTokenProvider.Token);
            }

            // #381: the PDF provider brackets each embedded-figure transcription with *[Image OCR p:N]*…*[End OCR]*
            // provenance markers (#371). These are no longer stripped — they are MarkItDown-style annotations that
            // stay in Document.Markdown (the egress payload) so any consumer — human, RAG engine, LLM — can see the
            // bracketed text came from OCR. The title generated below therefore runs over the marked Markdown, which
            // is fine: the markers are ordinary italic-annotated content, exactly as a re-ingested MarkItDown document
            // already carries them.
            var title = await TryGenerateTitleAsync(result.Markdown, _cancellationTokenProvider.Token)
                ?? MarkdownTitleExtractor.ExtractTitle(result.Markdown)
                ?? FallbackTitleFromFileName(workItem.OriginalFileName);

            // External phase (no UoW): archive native payload into blob storage + assemble Domain provenance metadata.
            // Archiving fails open: over limit / write failure / disabled archive affects only the manifest, not text extraction completion below.
            var extractionMetadata = await ArchiveNativePayloadAndBuildMetadataAsync(args.DocumentId, result);

            await CompleteRunAsync(args.DocumentId, workItem.RunId, result, title, extractionMetadata);
        }
        catch (Exception ex)
        {
            await FailRunAsync(args.DocumentId, workItem.RunId, ex.Message, VaultExtractPipelines.Parse);
            throw;
        }
    }

    private async Task<ParseWorkItem> BeginRunAsync(DocumentParseJobArgs args)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var document = await DocumentRepository.GetAsync(args.DocumentId, includeDetails: false);
        var run = await PipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, VaultExtractPipelines.Parse);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        // A derived sub-document (OriginDocumentId set) seeds its Markdown from the source constituent rather than
        // re-extracting it (#371): the unified detection pass persisted that constituent as a DocumentSegment keyed
        // by the shared OriginConstituentKey (SHA-256 of the clean span). A figure-kind segment's SliceText is the
        // figure's transcription, a text-kind segment's is the constituent text — either way the exact (clean) slice,
        // so there is no content drift. Loaded inside this UoW; null (not derived, or the slice is missing/empty)
        // falls back to the normal extraction path in ExecuteAsync.
        string? seedMarkdown = null;
        string? seedProviderName = null;
        if (document.OriginDocumentId.HasValue && !string.IsNullOrEmpty(document.OriginConstituentKey))
        {
            var segment = await _documentSegmentRepository.FirstOrDefaultAsync(
                s => s.SourceDocumentId == document.OriginDocumentId.Value && s.SegmentKey == document.OriginConstituentKey);
            if (!string.IsNullOrEmpty(segment?.SliceText))
            {
                seedMarkdown = segment.SliceText;
                seedProviderName = ScenarioBSegmentSeedProviderName;
            }
        }

        await uow.CompleteAsync();

        return new ParseWorkItem(
            run.Id,
            document.FileOrigin?.BlobName,
            document.FileOrigin?.ContentType,
            document.FileOrigin?.OriginalFileName,
            seedMarkdown,
            seedProviderName);
    }

    private async Task CompleteRunAsync(
        Guid documentId,
        Guid runId,
        TextExtractionResult result,
        string? title,
        DocumentParseMetadata extractionMetadata)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var (document, run) = await LoadDocumentAndRunAsync(
            documentId, runId, VaultExtractPipelines.Parse);

        await PipelineRunManager.CompleteParseAsync(
            document, run, result.Markdown, title,
            language: result.DetectedLanguage,
            extractionMetadata: extractionMetadata);

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
        // plus operator rerun / re-upload. Figure sub-document routing now rides classification (#371): the embedded
        // *[Image OCR]* markers make the classifier flag an embedded document and enqueue the unified pass — no
        // separate figure-routing enqueue here anymore.
        await _pipelineJobScheduler.QueueAsync(document, VaultExtractPipelines.Classification);

        // #265: after text extraction succeeds and Markdown is ready, fan out the "AI fallback cabinet selection when empty" job.
        // It is an independent sibling orthogonal to the content pipeline, not a PipelineRun phase. Do <b>not</b> read document.CabinetId here:
        // the text extraction job must not touch cabinet state (#194 guardrail). Manual / race gating is handled entirely inside
        // DocumentCabinetSuggestionBackgroundJob.
        // "One-shot" behavior (#265 guardrail 3) is naturally guaranteed by the Markdown write-once invariant:
        // CompleteParseAsync -> SetMarkdown throws MarkdownIsImmutable when Markdown already exists -> FailRun.
        // Therefore this success path can be hit <b>at most once</b> per document.
        // Retry is allowed only for Failed runs, with the first success happening here, and rerecognize re-enqueues only classification
        // without rerunning text extraction, so neither duplicates the fan-out.
        // Do not gate by AttemptNumber==1: that would miss the first-success case where the first attempt failed and retry succeeded
        // with successful run AttemptNumber > 1.
        await _backgroundJobManager.EnqueueAsync(
            new DocumentCabinetSuggestionJobArgs { DocumentId = document.Id, TenantId = document.TenantId });

        await uow.CompleteAsync();
    }

    /// <summary>
    /// External phase (no UoW): archives the winning provider's native payload into blob storage using a stable per-document key
    /// that is overwritten by re-extraction, and assembles the Domain typed metadata value object (#210).
    /// <para>
    /// <b>Archiving fails open</b>: no payload / over limit / blob write failure -> log warning and set manifest null.
    /// <see cref="DocumentParseMetadata"/> with provider name <b>still returns normally</b>, and text extraction continues successfully.
    /// Raw spatial signals such as bbox stay in blob storage; the DB stores only the manifest.
    /// </para>
    /// </summary>
    protected virtual async Task<DocumentParseMetadata> ArchiveNativePayloadAndBuildMetadataAsync(
        Guid documentId,
        TextExtractionResult result)
    {
        var manifest = await TryArchiveNativePayloadAsync(documentId, result.NativePayload);
        return new DocumentParseMetadata(
            result.ProviderName, manifest, result.IsComplete, result.IncompleteReason);
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
            // A raw markdown[..N] slice can land between the halves of a surrogate pair and emit a lone surrogate
            // into the prompt. Classification and cabinet suggestion always cut at a char boundary; title generation
            // did not. Use the shared helper (#491).
            var truncated = TextTruncator.AtCharBoundary(markdown, _behaviorOptions.MaxTitleGenerationMarkdownLength);

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

            // #491: same reason as the input cut above — and here the lone surrogate would be persisted. Cutting to
            // exactly MaxTitleLength keeps Document.SetTitle's surrogate guard from ever firing (it is gated on
            // `> MaxTitleLength`), so a split pair would reach the DB and the MCP egress.
            return TextTruncator.AtCharBoundary(title, DocumentConsts.MaxTitleLength);
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
        // #491: a file name can carry astral-plane characters; cut at a char boundary (see TryGenerateTitleAsync).
        return TextTruncator.AtCharBoundary(trimmed, DocumentConsts.MaxTitleLength);
    }

    private sealed record ParseWorkItem(
        Guid RunId,
        string? BlobName,
        string? ContentType,
        string? OriginalFileName,
        string? SeedMarkdown,
        string? SeedProviderName);
}

public class DocumentParseJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? PipelineRunId { get; set; }
}

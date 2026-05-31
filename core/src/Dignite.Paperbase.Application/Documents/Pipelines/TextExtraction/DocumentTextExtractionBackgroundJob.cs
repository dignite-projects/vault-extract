using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.TextExtraction;

[BackgroundJobName("Paperbase.DocumentTextExtraction")]
public class DocumentTextExtractionBackgroundJob
    : AsyncBackgroundJob<DocumentTextExtractionJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineRunAccessor _pipelineRunAccessor;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    /// <summary>
    /// Document-title generation is single-shot, tool-free, prompt-unique. Reuses the
    /// host-registered <see cref="PaperbaseAIConsts.TitleGeneratorChatClientKey"/> client
    /// (no FunctionInvocation, no DistributedCache) so traces stay clean and hosts can
    /// optionally point title generation at a smaller / cheaper model.
    /// </summary>
    private readonly IChatClient _titleGeneratorChatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly PaperbaseAIBehaviorOptions _behaviorOptions;

    public DocumentTextExtractionBackgroundJob(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        ITextExtractor textExtractor,
        IBlobContainer<PaperbaseDocumentContainer> blobContainer,
        IUnitOfWorkManager unitOfWorkManager,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        [FromKeyedServices(PaperbaseAIConsts.TitleGeneratorChatClientKey)] IChatClient titleGeneratorChatClient,
        IPromptProvider promptProvider,
        IOptions<PaperbaseAIBehaviorOptions> behaviorOptions)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _pipelineRunAccessor = pipelineRunAccessor;
        _pipelineJobScheduler = pipelineJobScheduler;
        _textExtractor = textExtractor;
        _blobContainer = blobContainer;
        _unitOfWorkManager = unitOfWorkManager;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _titleGeneratorChatClient = titleGeneratorChatClient;
        _promptProvider = promptProvider;
        _behaviorOptions = behaviorOptions.Value;
    }

    public override async Task ExecuteAsync(DocumentTextExtractionJobArgs args)
    {
        var workItem = await BeginRunAsync(args);

        try
        {
            var blobStream = await _blobContainer.GetAsync(workItem.OriginalFileBlobName);
            var ctx = new TextExtractionContext
            {
                ContentType = workItem.ContentType,
                FileExtension = Path.GetExtension(workItem.OriginalFileName ?? string.Empty),
                LanguageHints = { "ja", "en" }
            };

            var result = await _textExtractor.ExtractAsync(blobStream, ctx);

            var title = await TryGenerateTitleAsync(result.Markdown)
                ?? MarkdownTitleExtractor.ExtractTitle(result.Markdown)
                ?? FallbackTitleFromFileName(workItem.OriginalFileName);

            // External 段（无 UoW）：归档原生 payload 进 blob + 组装 Domain provenance 元数据。
            // 归档 fail-open——超限 / 写失败 / 关闭只影响 manifest，不影响下面的文本提取完成。
            var extractionMetadata = await ArchiveNativePayloadAndBuildMetadataAsync(args.DocumentId, result);

            await CompleteRunAsync(
                args.DocumentId, workItem.RunId, result, title, extractionMetadata);
        }
        catch (Exception ex)
        {
            await FailRunAsync(args.DocumentId, workItem.RunId, ex.Message);
        }
    }

    private async Task<TextExtractionWorkItem> BeginRunAsync(DocumentTextExtractionJobArgs args)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(args.DocumentId);
        var run = await _pipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, PaperbasePipelines.TextExtraction);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return new TextExtractionWorkItem(
            run.Id,
            document.OriginalFileBlobName,
            document.FileOrigin.ContentType,
            document.FileOrigin.OriginalFileName);
    }

    private async Task CompleteRunAsync(
        Guid documentId,
        Guid runId,
        TextExtractionResult result,
        string? title,
        DocumentTextExtractionMetadata extractionMetadata)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(documentId);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.TextExtraction);

        await _pipelineRunManager.CompleteTextExtractionAsync(
            document, run, result.Markdown, title,
            language: result.DetectedLanguage,
            extractionMetadata: extractionMetadata);

        // 发布 OCRCompletedEto——薄载荷，下游通过 REST 回拉 Markdown。
        await _distributedEventBus.PublishAsync(
            new OCRCompletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now,
                UsedOcr = result.UsedOcr
            });

        // 文本提取完成即推进分类——OCR 不设质量门控
        // （#196：OCR 平均置信度预测不了真实质量；质量问题由分类审核 + 操作员重跑/重传事后处理）。
        await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.Classification);

        await uow.CompleteAsync();
    }

    /// <summary>
    /// External 段（无 UoW）：把胜出 provider 的原生 payload 归档进 blob（稳定 per-document key，重提取覆盖），
    /// 并组装 Domain typed 元数据值对象（#210）。
    /// <para>
    /// <b>归档 fail-open</b>：无 payload / 超限 / blob 写失败 → 记 warning、manifest 置 null，
    /// <see cref="DocumentTextExtractionMetadata"/>（provider 名）<b>照常返回</b>，文本提取继续成功。
    /// 原始 bbox 等空间信号留 blob，DB 只存 manifest。
    /// </para>
    /// </summary>
    protected virtual async Task<DocumentTextExtractionMetadata> ArchiveNativePayloadAndBuildMetadataAsync(
        Guid documentId,
        TextExtractionResult result)
    {
        var manifest = await TryArchiveNativePayloadAsync(documentId, result.NativePayload);
        return new DocumentTextExtractionMetadata(result.ProviderName, manifest);
    }

    private async Task<NativePayloadManifest?> TryArchiveNativePayloadAsync(Guid documentId, NativePayload? payload)
    {
        if (payload is null || payload.Content is not { Length: > 0 } content)
        {
            return null;
        }

        // ContentType / SchemaName 缺失 → manifest 构造器（Check.NotNullOrWhiteSpace）会抛，但 NativePayload 契约本身不强制非空
        // （未来 rich Markdown / 第三方 provider 可能半填）。在写 blob 之前 fail-open 退出：辅助审计 blob 绝不打挂主 Markdown
        // pipeline，也不留下登记不上的孤儿 blob。
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

        // 稳定 per-document key：重提取覆盖（一个文档一个归档 blob，避免孤儿）。
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

            var template = _promptProvider.GetTitleGenerationPrompt(_behaviorOptions.DefaultLanguage);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, template.SystemInstructions),
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
    /// Markdown 标题抽取失败时的确定性回退：使用不带扩展名的原始文件名。
    /// 仍然为空（极端情况下 FileOrigin.OriginalFileName 为 null）则返回 null，让 UI 沿用原有文件名/blob 名展示。
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

    private async Task FailRunAsync(Guid documentId, Guid runId, string errorMessage)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetWithPipelineRunsAsync(documentId);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.TextExtraction);

        await _pipelineRunManager.FailAsync(document, run, errorMessage);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    private sealed record TextExtractionWorkItem(
        Guid RunId,
        string OriginalFileBlobName,
        string ContentType,
        string? OriginalFileName);
}

public class DocumentTextExtractionJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }
}

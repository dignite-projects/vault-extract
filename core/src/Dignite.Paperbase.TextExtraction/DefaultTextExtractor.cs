using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.TextExtraction;

public class DefaultTextExtractor : ITextExtractor, ITransientDependency
{
    private readonly IOcrProvider _ocrProvider;
    private readonly IMarkdownTextProvider _markdownProvider;
    private readonly PaperbaseOcrOptions _ocrOptions;

    public ILogger<DefaultTextExtractor> Logger { get; set; } = NullLogger<DefaultTextExtractor>.Instance;

    public DefaultTextExtractor(
        IOcrProvider ocrProvider,
        IMarkdownTextProvider markdownProvider,
        IOptions<PaperbaseOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _markdownProvider = markdownProvider;
        _ocrOptions = ocrOptions.Value;
    }

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        if (IsImageFormat(context.FileExtension))
        {
            return await ExtractByOcrAsync(fileStream, context, cancellationToken);
        }

        // 用单一 MemoryStream 横跨 Markdown Provider + 可能的 OCR 回退两次读取：
        // 输入流来自 blob 存储可能不可 seek，且 ElBruno 内部 PdfPig/OpenXml 等
        // 解析器要求 seekable stream，故必须缓冲。
        // 已知限制：超大文件（GB 级扫描 PDF）会全量驻留内存，需要时改为临时文件路径。
        using var seekable = new MemoryStream();
        await fileStream.CopyToAsync(seekable, cancellationToken);
        seekable.Position = 0;

        var md = await _markdownProvider.ExtractAsync(seekable, context, cancellationToken);

        if (!HasMeaningfulText(md.Markdown) && IsPdfExtension(context.FileExtension))
        {
            Logger.LogDebug("Markdown provider produced no meaningful text for PDF; falling back to OCR.");
            seekable.Position = 0;
            return await ExtractByOcrAsync(seekable, context, cancellationToken);
        }

        Logger.LogDebug("Markdown extraction completed using {Provider}", _markdownProvider.GetType().Name);
        return md;
    }

    protected virtual async Task<TextExtractionResult> ExtractByOcrAsync(
        Stream fileStream,
        TextExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        Stream seekable;
        bool ownsStream;
        if (fileStream is MemoryStream { CanSeek: true })
        {
            seekable = fileStream;
            ownsStream = false;
        }
        else
        {
            var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms, cancellationToken);
            seekable = ms;
            ownsStream = true;
        }

        try
        {
            var languageHints = ctx.LanguageHints?.Count > 0
                ? ctx.LanguageHints
                : (IList<string>)_ocrOptions.DefaultLanguageHints;

            seekable.Position = 0;
            var result = await _ocrProvider.RecognizeAsync(seekable, new OcrOptions
            {
                ContentType = ctx.ContentType ?? string.Empty,
                LanguageHints = languageHints
            }, cancellationToken);

            Logger.LogDebug("OCR completed using {Provider}.", result.ProviderName ?? _ocrProvider.GetType().Name);

            return new TextExtractionResult
            {
                Markdown = result.Markdown,
                DetectedLanguage = result.DetectedLanguage,
                UsedOcr = true,
                ProviderName = result.ProviderName,
                IsComplete = result.IsComplete,
                IncompleteReason = result.IncompleteReason,
                NativePayload = MapNativePayload(result)
            };
        }
        finally
        {
            if (ownsStream)
            {
                await seekable.DisposeAsync();
            }
        }
    }

    // OcrResult → TextExtractionResult 的原生 payload 跨契约映射（Ocr 项目不引用 Abstractions；
    // 扁平字段避免创建两遍相同的包装类，与 Markdown/DetectedLanguage 等字段一样在编排层映射）。
    private NativePayload? MapNativePayload(OcrResult result)
    {
        if (result.NativePayloadContent is not { Length: > 0 } content)
        {
            // 无 payload（provider 无空间模型）——正常路径，静默。
            return null;
        }

        if (string.IsNullOrEmpty(result.NativePayloadContentType) || string.IsNullOrEmpty(result.NativePayloadSchemaName))
        {
            // 有内容但缺 ContentType / SchemaName：扁平字段被 provider 半填，归档无法标注 schema。
            // 丢弃但记 warning——不静默吞，否则空间信号丢失而无任何线索。
            Logger.LogWarning(
                "OCR provider {Provider} produced {Bytes} bytes of native payload but left ContentType/SchemaName unset; dropping it.",
                result.ProviderName, content.Length);
            return null;
        }

        return new NativePayload(content, result.NativePayloadContentType, result.NativePayloadSchemaName);
    }

    protected virtual bool IsImageFormat(string? fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension)) return false;
        var ext = fileExtension.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".tiff" or ".tif" or ".bmp" or ".webp" or ".gif";
    }

    protected virtual bool HasMeaningfulText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        return markdown.Any(c => char.IsLetter(c) || char.IsDigit(c));
    }

    protected virtual bool IsPdfExtension(string? fileExtension)
        => string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);
}

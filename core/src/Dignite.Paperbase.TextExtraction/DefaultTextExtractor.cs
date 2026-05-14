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
            return await ExtractByOcrAsync(fileStream, context);
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
            return await ExtractByOcrAsync(seekable, context);
        }

        Logger.LogDebug("Markdown extraction completed using {Provider}", _markdownProvider.GetType().Name);
        return md;
    }

    protected virtual async Task<TextExtractionResult> ExtractByOcrAsync(
        Stream fileStream,
        TextExtractionContext ctx)
    {
        var options = new OcrOptions
        {
            ContentType = ctx.ContentType ?? string.Empty,
            LanguageHints = ctx.LanguageHints?.Count > 0
                ? ctx.LanguageHints
                : (IList<string>)_ocrOptions.DefaultLanguageHints
        };

        var result = await _ocrProvider.RecognizeAsync(fileStream, options);

        Logger.LogDebug("OCR extraction completed using {Provider}", _ocrProvider.GetType().Name);

        return new TextExtractionResult
        {
            Markdown = result.Markdown,
            Confidence = result.Confidence,
            DetectedLanguage = result.DetectedLanguage,
            PageCount = result.PageCount,
            UsedOcr = true,
        };
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

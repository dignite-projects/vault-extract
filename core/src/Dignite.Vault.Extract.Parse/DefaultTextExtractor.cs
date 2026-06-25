using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Parse;

public class DefaultTextExtractor : ITextExtractor, ITransientDependency
{
    private readonly IOcrProvider _ocrProvider;
    private readonly IReadOnlyList<IMarkdownTextProvider> _markdownProviders;
    private readonly ExtractOcrOptions _ocrOptions;

    public ILogger<DefaultTextExtractor> Logger { get; set; } = NullLogger<DefaultTextExtractor>.Instance;

    public DefaultTextExtractor(
        IOcrProvider ocrProvider,
        IEnumerable<IMarkdownTextProvider> markdownProviders,
        IOptions<ExtractOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _markdownProviders = markdownProviders.ToList();
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

        // Use one MemoryStream across the Markdown provider and possible OCR fallback, both of which
        // may read the stream. Input streams from blob storage may be non-seekable, and parsers inside
        // ElBruno such as PdfPig/OpenXml require a seekable stream, so buffering is required. Known
        // limit: very large files, such as GB-scale scanned PDFs, stay fully in memory; switch to a
        // temporary file path if needed.
        using var seekable = new MemoryStream();
        await fileStream.CopyToAsync(seekable, cancellationToken);
        seekable.Position = 0;

        var markdownProvider = SelectMarkdownProvider(context.FileExtension);
        var md = await markdownProvider.ExtractAsync(seekable, context, cancellationToken);

        // PDFs without a usable text layer (scanned / image-only) yield no meaningful text from the
        // Markdown provider. Fall back to whole-page OCR. A PdfPig-based provider deliberately returns
        // empty here for a no-text-layer PDF instead of OCR-ing embedded images itself, so this single
        // fallback owns the scanned-PDF path (no double OCR).
        if (!HasMeaningfulText(md.Markdown) && IsPdfExtension(context.FileExtension))
        {
            Logger.LogDebug("Markdown provider produced no meaningful text for PDF; falling back to OCR.");
            seekable.Position = 0;
            return await ExtractByOcrAsync(seekable, context, cancellationToken);
        }

        Logger.LogDebug("Markdown extraction completed using {Provider}", markdownProvider.GetType().Name);
        return md;
    }

    /// <summary>
    /// Selects the Markdown provider for a file extension: among providers that <see cref="IMarkdownTextProvider.CanHandle"/>
    /// the extension, the highest <see cref="IMarkdownTextProvider.Priority"/> wins. Providers coexist and
    /// are dispatched per file (unlike the mutually-exclusive <see cref="IOcrProvider"/>); the catch-all
    /// fallback (ElBruno) handles every extension at the lowest priority, so a missing specialized module
    /// degrades gracefully.
    /// </summary>
    protected virtual IMarkdownTextProvider SelectMarkdownProvider(string? fileExtension)
    {
        var ext = fileExtension ?? string.Empty;

        IMarkdownTextProvider? selected = null;
        foreach (var provider in _markdownProviders)
        {
            if (!provider.CanHandle(ext))
            {
                continue;
            }

            if (selected is null || provider.Priority > selected.Priority)
            {
                selected = provider;
            }
            else if (provider.Priority == selected.Priority)
            {
                // Two providers claim the same extension at the same priority: a deployment/config
                // mistake. Pick deterministically (keep the incumbent) but surface it loudly.
                Logger.LogWarning(
                    "Multiple Markdown providers claim extension '{Extension}' at priority {Priority}: keeping {Incumbent}, ignoring {Candidate}.",
                    ext, provider.Priority, selected.GetType().Name, provider.GetType().Name);
            }
        }

        if (selected is null)
        {
            // Cannot happen while a catch-all provider (ElBruno) is installed. If it does, it is a
            // module-composition error, not a per-document failure — fail loudly.
            throw new InvalidOperationException(
                $"No {nameof(IMarkdownTextProvider)} can handle extension '{ext}'. " +
                $"Install a catch-all Markdown provider module (e.g. ElBruno) in the host.");
        }

        return selected;
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

    // Cross-contract mapping from OcrResult native payload to TextExtractionResult. The Ocr project
    // does not reference Abstractions; flat fields avoid creating the same wrapper class twice and are
    // mapped in the orchestration layer like Markdown / DetectedLanguage.
    private NativePayload? MapNativePayload(OcrResult result)
    {
        if (result.NativePayloadContent is not { Length: > 0 } content)
        {
            // No payload, usually because the provider has no spatial model. This is a normal path.
            return null;
        }

        if (string.IsNullOrEmpty(result.NativePayloadContentType) || string.IsNullOrEmpty(result.NativePayloadSchemaName))
        {
            // Content exists but ContentType / SchemaName is missing: the provider half-filled the
            // flat fields and archival cannot label the schema. Drop it, but log a warning instead of
            // swallowing silently, otherwise spatial signals disappear without evidence.
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

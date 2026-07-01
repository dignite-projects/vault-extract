using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Ocr.VisionLlm;

/// <summary>
/// Vendor-agnostic OCR provider built on a multimodal <see cref="IChatClient"/> (vision LLM).
/// Solves the "phone photo / thermal receipt" scenario where layout OCR (PP-StructureV3) fails outright
/// (#259). Feeds the image to the host-registered keyed vision client + compile-time instructions and
/// returns the transcription as Markdown — preserving the Markdown-first channel contract.
/// <para>
/// Scope (v1): images and scanned / image-only PDFs (rasterized page by page). Digital PDFs with a text
/// layer never reach OCR — the Markdown provider handles them. Out-of-band spatial signals (bbox/cells)
/// do not exist for a chat LLM, so <see cref="OcrResult"/> native-payload fields stay null.
/// </para>
/// </summary>
public class VisionLlmOcrProvider : IOcrProvider, ITransientDependency
{
    /// <summary>Provider family name surfaced on <see cref="OcrResult.ProviderName"/> for auditability.</summary>
    public const string ProviderFamilyName = "VisionLlm";

    private readonly IChatClient _chatClient;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly VisionLlmOcrOptions _options;

    public ILogger<VisionLlmOcrProvider> Logger { get; set; } = NullLogger<VisionLlmOcrProvider>.Instance;

    public VisionLlmOcrProvider(
        [FromKeyedServices(VisionLlmOcrConsts.VisionChatClientKey)] IChatClient chatClient,
        IPdfRasterizer pdfRasterizer,
        IOptions<VisionLlmOcrOptions> options)
    {
        _chatClient = chatClient;
        _pdfRasterizer = pdfRasterizer;
        _options = options.Value;
    }

    public virtual async Task<OcrResult> RecognizeAsync(
        Stream fileStream,
        OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllBytesAsync(fileStream, cancellationToken);

        var outcome = DetectInputKind(options.ContentType, bytes) switch
        {
            InputKind.Image => await RecognizeImageAsync(
                bytes, ResolveImageMediaType(options.ContentType, bytes), cancellationToken),
            InputKind.Pdf => await RecognizePdfAsync(bytes, cancellationToken),
            _ => HandleUnsupported(options.ContentType)
        };

        return new OcrResult
        {
            Markdown = outcome.Markdown,
            ProviderName = ProviderFamilyName,
            // #268: surface whether the transcription is complete (truncation / dropped pages / discarded loop).
            IsComplete = outcome.IsComplete,
            IncompleteReason = outcome.IncompleteReason
            // NativePayload* left null: a chat vision LLM has no out-of-band spatial model (bbox / cell).
            // DetectedLanguage left null: language detection is not this provider's responsibility.
        };
    }

    /// <summary>Single-image transcription: one vision-LLM call, guarded against repetition loops.</summary>
    protected virtual async Task<ImageTranscription> RecognizeImageAsync(
        byte[] imageBytes,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, VisionLlmOcrInstructions.SystemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent(VisionLlmOcrInstructions.UserPrompt),
                new DataContent(imageBytes, mediaType)
            })
        };

        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature = _options.Temperature
        };

        var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        // #448: a chat vision LLM sometimes wraps all or part of its Markdown in a ```markdown code fence
        // despite the prompt forbidding it, which makes the fenced block (typically a table) render as literal
        // code downstream. Strip fence delimiters so the persisted transcription is clean Markdown for every
        // consumer (preview / RAG / classification / egress). Trim again since removing a leading/trailing
        // fence line can leave a blank edge.
        var text = VisionLlmOutputGuard.StripCodeFences(response.Text).Trim();

        if (text.Length == 0)
        {
            // Genuinely blank (the model returned nothing) — complete: there was nothing to capture.
            return ImageTranscription.Complete(string.Empty);
        }

        // Hallucination / repetition-loop guard: never persist a runaway loop as if it were real text.
        if (VisionLlmOutputGuard.LooksLikeRepetitionLoop(
                text,
                _options.MaxConsecutiveRepeatedLines,
                _options.MinDistinctLineRatio,
                _options.MinLinesForRatioCheck,
                _options.MinLengthForSegmentCheck,
                _options.MaxRepeatedSegmentLength,
                _options.MinRepeatedSegmentRepeats))
        {
            Logger.LogWarning(
                "VisionLlm OCR output tripped the repetition guard ({Length} chars, finishReason={FinishReason}); discarding to avoid persisting a hallucination loop.",
                text.Length, response.FinishReason);
            return ImageTranscription.Incomplete(string.Empty, "Output discarded as a suspected repetition loop.");
        }

        // Hit the token cap without a detected loop: could be a genuinely dense page. Keep the
        // (possibly truncated) transcription but mark it incomplete so downstream knows the tail may be missing.
        if (response.FinishReason == ChatFinishReason.Length)
        {
            Logger.LogWarning(
                "VisionLlm OCR output hit MaxOutputTokens={MaxOutputTokens} (finishReason=Length); the transcription may be truncated.",
                _options.MaxOutputTokens);
            return ImageTranscription.Incomplete(
                text, $"Output truncated at the token limit (MaxOutputTokens={_options.MaxOutputTokens}).");
        }

        return ImageTranscription.Complete(text);
    }

    /// <summary>
    /// Scanned / image-only PDF: rasterize each page to PNG and transcribe it. A PDF exceeding
    /// <see cref="VisionLlmOcrOptions.MaxPdfPages"/> fails loudly rather than silently dropping pages.
    /// </summary>
    protected virtual async Task<ImageTranscription> RecognizePdfAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        var pageCount = _pdfRasterizer.GetPageCount(pdfBytes);
        if (pageCount <= 0)
        {
            Logger.LogWarning("VisionLlm OCR: PDF reported {PageCount} pages; nothing to transcribe.", pageCount);
            return ImageTranscription.Incomplete(string.Empty, "PDF reported no pages.");
        }

        if (pageCount > _options.MaxPdfPages)
        {
            throw new InvalidOperationException(
                $"VisionLlm OCR: PDF has {pageCount} pages, exceeding the configured MaxPdfPages={_options.MaxPdfPages}. " +
                "Each page is a separate paid vision-LLM call; raise VisionLlmOcr:MaxPdfPages to process larger PDFs, " +
                "or route large scanned PDFs to a different OCR provider.");
        }

        var pages = new List<string>(pageCount);
        var incompletePages = 0;
        for (var i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] png;
            try
            {
                png = _pdfRasterizer.RenderPageToPng(pdfBytes, i);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single page PDFium cannot render (corrupt page object, font fault) must not discard the
                // other pages already transcribed. Skip it with a warning. Note: a failure in the LLM call
                // below is deliberately NOT caught here — that is almost always systemic (auth / network /
                // quota) and should fail the run loudly rather than be silently swallowed per page.
                Logger.LogWarning(
                    ex,
                    "VisionLlm OCR: page {Page}/{PageCount} failed to rasterize; skipping it.",
                    i + 1, pageCount);
                incompletePages++;
                continue;
            }

            var page = await RecognizeImageAsync(png, "image/png", cancellationToken);
            if (!string.IsNullOrWhiteSpace(page.Markdown))
            {
                pages.Add(page.Markdown);
                if (!page.IsComplete)
                {
                    // Kept but truncated at the token limit — content tail may be missing.
                    incompletePages++;
                }
            }
            else if (!page.IsComplete)
            {
                // Empty + incomplete = discarded by the guard (a loop). Empty + complete = a genuinely
                // blank page, which is fine and not counted against completeness.
                incompletePages++;
                Logger.LogWarning(
                    "VisionLlm OCR: page {Page}/{PageCount} produced no usable text (discarded by the repetition guard); skipping it.",
                    i + 1, pageCount);
            }
        }

        var combined = string.Join("\n\n", pages);
        return incompletePages == 0
            ? ImageTranscription.Complete(combined)
            : ImageTranscription.Incomplete(
                combined, $"{incompletePages} of {pageCount} page(s) were not fully transcribed.");
    }

    /// <summary>Non-image, non-PDF input: fail open with empty Markdown + a warning (document still persists).</summary>
    protected virtual ImageTranscription HandleUnsupported(string? contentType)
    {
        Logger.LogWarning(
            "VisionLlm OCR received unsupported input (contentType='{ContentType}'); this provider handles images and image-only PDFs only. Returning empty Markdown.",
            contentType);
        return ImageTranscription.Incomplete(string.Empty, "Unsupported input type for vision-LLM OCR.");
    }

    /// <summary>
    /// Outcome of transcribing one image (or an aggregated PDF): the Markdown plus whether it captured the
    /// complete content. Carried out of the per-image / per-page methods so <see cref="RecognizeAsync"/> can
    /// populate <see cref="OcrResult.IsComplete"/> / <see cref="OcrResult.IncompleteReason"/> (#268).
    /// </summary>
    protected readonly record struct ImageTranscription(string Markdown, bool IsComplete, string? IncompleteReason)
    {
        public static ImageTranscription Complete(string markdown) => new(markdown, true, null);
        public static ImageTranscription Incomplete(string markdown, string reason) => new(markdown, false, reason);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static InputKind DetectInputKind(string? contentType, byte[] bytes)
    {
        // Magic bytes are authoritative; the Content-Type is a client/storage-supplied hint and must NOT
        // override the file signature. A PDF mislabeled "image/png" must still take the PDF page path
        // (sending raw PDF bytes to the vision model as a single image would fail or yield wrong text).
        if (IsPdfMagic(bytes))
        {
            return InputKind.Pdf;
        }

        if (SniffImageMediaType(bytes) != null)
        {
            return InputKind.Image;
        }

        // No recognizable signature → fall back to the Content-Type hint.
        var ct = NormalizeContentType(contentType);
        if (ct == "application/pdf")
        {
            return InputKind.Pdf;
        }

        return ct.StartsWith("image/", StringComparison.Ordinal) ? InputKind.Image : InputKind.Unsupported;
    }

    private static string ResolveImageMediaType(string? contentType, byte[] bytes)
    {
        // Prefer the sniffed signature over a possibly-wrong Content-Type; fall back to an image/* hint
        // (for formats we do not sniff, e.g. HEIC / AVIF), then a safe default.
        var sniffed = SniffImageMediaType(bytes);
        if (sniffed != null)
        {
            return sniffed;
        }

        var ct = NormalizeContentType(contentType);
        return ct.StartsWith("image/", StringComparison.Ordinal) ? ct : "image/png";
    }

    private static string NormalizeContentType(string? contentType)
        => contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

    // Magic-byte detection lives in the shared Ocr-contract helper so the signature table is defined once
    // across all providers (was previously duplicated here / in PdfImagePayload / in PptxImagePayload).
    private static bool IsPdfMagic(byte[] b) => ImageSignature.IsPdf(b);

    private static string? SniffImageMediaType(byte[] b) => ImageSignature.SniffMediaType(b);

    private enum InputKind
    {
        Image,
        Pdf,
        Unsupported
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using Volo.Abp.DependencyInjection;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// PdfPig-based Markdown provider for digital PDFs (#301). Owns the full <c>.pdf</c> parsing pass so it
/// can extract the text layer (words + bbox + reading order) and the embedded raster image objects, then
/// transcribe each image through the host-selected <see cref="IOcrProvider"/> and inline the
/// transcription into the Markdown at the image's reading position. This closes the silent-image-loss gap
/// where embedded figures in digital PDFs never reached the channel output.
/// <para>
/// <b>Image → text uses <see cref="IOcrProvider"/> only</b> (no keyed Vision <c>IChatClient</c> here, no
/// new LLM call site). Semantics are transcription only; the figure's bytes are the OCR input, so there is
/// no user free-text entering a prompt and no <c>PromptBoundary</c> concern.
/// </para>
/// <para>
/// <b>Scope.</b> A PDF with no digital text layer (scanned / image-only) returns empty Markdown here and
/// is left to <c>DefaultTextExtractor</c>'s whole-page OCR fallback — this provider does not OCR images on
/// a text-less PDF, so there is no double OCR. Vector-only graphics are an accepted blind spot
/// (<c>GetImages()</c> does not see them).
/// </para>
/// </summary>
[ExposeServices(typeof(IMarkdownTextProvider))]
public class PdfExtractor : IMarkdownTextProvider, ITransientDependency
{
    /// <summary>Provider family name surfaced on <see cref="TextExtractionResult.ProviderName"/> for auditability.</summary>
    public const string ProviderIdentifier = "PdfPig";

    private readonly IOcrProvider _ocrProvider;
    private readonly PdfExtractorOptions _options;
    private readonly DocumentAIOcrOptions _ocrOptions;

    public ILogger<PdfExtractor> Logger { get; set; } = NullLogger<PdfExtractor>.Instance;

    public PdfExtractor(
        IOcrProvider ocrProvider,
        IOptions<PdfExtractorOptions> options,
        IOptions<DocumentAIOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _options = options.Value;
        _ocrOptions = ocrOptions.Value;
    }

    /// <inheritdoc/>
    public virtual bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public virtual int Priority => MarkdownProviderPriorities.Specialized;

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        var bytes = await TextExtractionStreams.ReadAllBytesAsync(fileStream, cancellationToken);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt / encrypted / not-actually-a-PDF: return empty Markdown so DefaultTextExtractor's
            // whole-page OCR fallback (which fires when a PDF yields no meaningful text) can try instead.
            Logger.LogWarning(ex, "PdfPig could not open the PDF ({Bytes} bytes); deferring to OCR fallback.", bytes.Length);
            return Empty();
        }

        using (document)
        {
            var pages = new List<PageContent>();
            var hasTextLayer = false;
            var failedPages = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<Word> words;
                try
                {
                    words = page.GetWords().ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // PdfPig parses content streams lazily, so a corrupt page / malformed font faults here
                    // (on access), not at Open. Skip the bad page and mark the result incomplete rather
                    // than failing the whole document — mirrors VisionLlmOcrProvider's per-page resilience (#268).
                    Logger.LogWarning(ex, "Failed to parse the text layer of a PDF page; skipping it.");
                    failedPages++;
                    continue;
                }

                if (!hasTextLayer && words.Any(w => HasLetterOrDigit(w.Text)))
                {
                    hasTextLayer = true;
                }

                pages.Add(new PageContent(page, words));
            }

            // No digital text layer → scanned / image-only PDF. Do NOT OCR images here; return empty so
            // DefaultTextExtractor's whole-page OCR fallback owns that path (avoids double OCR).
            if (!hasTextLayer)
            {
                Logger.LogDebug("PDF has no digital text layer; deferring to whole-page OCR fallback.");
                return Empty();
            }

            var pageMarkdowns = new List<string>(pages.Count);
            var imageBudget = _options.MaxImagesPerPdf;
            var droppedByCap = 0;
            var undecodable = 0;
            var truncatedOcr = 0;
            var failedFigureOcr = 0;
            // Distinct from failedPages (GetWords fault → whole page dropped): here GetImages faulted but
            // the page's text was already extracted and is retained — only its images are skipped.
            var pagesWithSkippedImages = 0;

            var languageHints = ResolveLanguageHints(context);

            foreach (var pageContent in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lines = PdfReadingOrder.GroupWordsIntoLines(pageContent.Words);
                var figures = new List<PdfReadingOrder.Figure>();

                List<IPdfImage> images;
                try
                {
                    images = pageContent.Page.GetImages().ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lazy content-stream parse can fault on a corrupt page object / font. Keep the page's
                    // already-extracted text, skip its images, and mark the result incomplete (#268) rather
                    // than failing the whole document.
                    Logger.LogWarning(ex, "Failed to read images on a PDF page; keeping its text, skipping its images.");
                    pagesWithSkippedImages++;
                    images = new List<IPdfImage>();
                }

                foreach (var image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsTooSmall(image))
                    {
                        // Decorative icon / bullet / rule / spacer — not figure content, not counted incomplete.
                        continue;
                    }

                    if (IsFullPageScanBackground(pageContent.Page, image, pageContent.Words))
                    {
                        // Searchable / "sandwich" PDF (#309): this image is the full-page scan raster and the
                        // page's text layer is its OCR transcription. Re-OCRing it would duplicate text already
                        // extracted and burn a redundant (paid) vision call. Skip it WITHOUT OCR, WITHOUT
                        // emitting a figure, and WITHOUT tripping the completeness signal — an intentional,
                        // non-lossy skip (the text layer is retained), not a dropped figure. The guard is
                        // high-precision and errs toward keeping, so a real full-page figure is never dropped.
                        continue;
                    }

                    if (imageBudget <= 0)
                    {
                        droppedByCap++;
                        continue;
                    }

                    (byte[] Bytes, string ContentType)? payload;
                    try
                    {
                        payload = PdfImagePayload.TryResolve(image);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.LogWarning(ex, "Failed to decode an embedded PDF image; skipping it.");
                        payload = null;
                    }

                    if (payload is null)
                    {
                        // Unsupported codec (JBIG2 / JPX / CCITT) or undecodable raw bitmap.
                        undecodable++;
                        continue;
                    }

                    imageBudget--;

                    OcrResult ocr;
                    try
                    {
                        using var imageStream = new MemoryStream(payload.Value.Bytes, writable: false);
                        ocr = await _ocrProvider.RecognizeAsync(
                            imageStream,
                            new OcrOptions
                            {
                                ContentType = payload.Value.ContentType,
                                LanguageHints = languageHints
                            },
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A single figure's OCR failing (provider timeout / rate-limit / auth / one bad image)
                        // must NOT discard the digital text layer already extracted — figure OCR is an
                        // auxiliary augmentation here, not the document's primary payload (the #210/#268
                        // "an auxiliary step must not break the main pipeline" principle). Skip this figure
                        // and mark the result incomplete. OperationCanceledException still propagates so a
                        // host/job shutdown aborts promptly.
                        Logger.LogWarning(ex, "Embedded-image OCR failed; keeping the text layer, skipping this figure.");
                        failedFigureOcr++;
                        continue;
                    }

                    if (!ocr.IsComplete)
                    {
                        // OCR truncated at the token limit or discarded by the repetition guard.
                        truncatedOcr++;
                    }

                    var transcription = ocr.Markdown?.Trim() ?? string.Empty;
                    if (transcription.Length > 0)
                    {
                        figures.Add(new PdfReadingOrder.Figure(image.BoundingBox, transcription));
                    }
                }

                var pageMarkdown = PdfReadingOrder.Render(lines, figures);
                if (!string.IsNullOrWhiteSpace(pageMarkdown))
                {
                    pageMarkdowns.Add(pageMarkdown);
                }
            }

            // Single source of truth for the #268 signal: the reason string is built from the counters and
            // is null iff nothing was lost; completeness derives from it (no parallel hand-synced predicate
            // that could drift when a new counter is added).
            var incompleteReason = BuildIncompleteReason(
                failedPages, pagesWithSkippedImages, droppedByCap, undecodable, truncatedOcr, failedFigureOcr);
            var complete = incompleteReason is null;
            if (!complete)
            {
                Logger.LogWarning("PDF extraction incomplete: {Reason}", incompleteReason);
            }

            return new TextExtractionResult
            {
                Markdown = string.Join("\n\n", pageMarkdowns),
                DetectedLanguage = null,
                // UsedOcr means "scan vs digital" per its contract (true = physical-scan OCR,
                // false = direct digital text layer), NOT "was any OCR call made". A digital PDF reports
                // false even when embedded figures were transcribed via IOcrProvider: the document is a
                // digital extraction; figure OCR is auxiliary. The binary field therefore cannot express
                // the new "digital + figure OCR" state introduced in #301 — a dedicated figure-OCR signal
                // (e.g. UsedFigureOcr) is deferred to the TextExtractionResult contract-evolution round
                // (with the out-of-band Figures signal, #306). Do NOT flip this to true: that would
                // misreport a digital document as a physical scan to "scan vs digital" consumers.
                UsedOcr = false,
                ProviderName = ProviderIdentifier,
                IsComplete = complete,
                IncompleteReason = incompleteReason,
                // PdfPig text layer + per-image OCR has no single aggregated spatial payload to archive
                // this round (#210). Left null deliberately.
                NativePayload = null
            };
        }
    }

    /// <summary>Whether an embedded image is below the <see cref="PdfExtractorOptions.MinImagePixels"/> threshold (decorative).</summary>
    protected virtual bool IsTooSmall(IPdfImage image)
    {
        var pixels = (long)image.WidthInSamples * image.HeightInSamples;
        return pixels < _options.MinImagePixels;
    }

    /// <summary>
    /// High-precision guard for the searchable / "sandwich" PDF case (#309): a full-page scan raster whose
    /// content is already present as the page's (often invisible) OCR text layer. Returns <c>true</c> only
    /// on a strong redundancy signal so a real full-page figure (figure + caption / infographic / photo) is
    /// never dropped — <b>when unsure it returns <c>false</c></b> (keep + OCR), because dropping a real
    /// figure is lossy and unacceptable whereas an occasional missed skip is merely a wasted OCR call.
    /// <para>Two signals must BOTH hold:</para>
    /// <list type="number">
    /// <item><b>Full-page placement.</b> The image's placement bbox (<c>image.BoundingBox</c>, NOT pixel
    /// <c>WidthInSamples × HeightInSamples</c>) covers at least
    /// <see cref="PdfExtractorOptions.FullPageScanCoverageThreshold"/> of the page box on both width and
    /// height.</item>
    /// <item><b>Redundant text layer.</b> The page's text over the image region reads as a whole-page
    /// transcription: it spans at least <see cref="PdfExtractorOptions.FullPageScanMinTextVerticalCoverage"/>
    /// of the image height (rejecting a thin edge caption band — the primary anti-false-positive guard) AND
    /// it is either many lines (<see cref="PdfExtractorOptions.FullPageScanMinTextLines"/>) or a
    /// predominantly invisible <c>Tr 3</c> layer (<see cref="PdfExtractorOptions.FullPageScanMinInvisibleTextRatio"/>
    /// — the canonical sandwich signature; a bonus that is sufficient but not necessary).</item>
    /// </list>
    /// Out of scope (left to double-OCR for now, #309): tiled multi-image scan pages, heavily
    /// margined/cropped scans whose single image does not clear the coverage bar, and rotated placements
    /// (PdfPig's PdfRectangle edge accessors are not rotation-aware, so the geometry math no-ops). All fail
    /// safe — they re-OCR rather than ever dropping a figure.
    /// </summary>
    protected virtual bool IsFullPageScanBackground(Page page, IPdfImage image, IReadOnlyList<Word> words)
    {
        if (!_options.SkipFullPageScanBackground)
        {
            return false;
        }

        var imageBox = image.BoundingBox;
        var imageWidth = Math.Abs(imageBox.Width);
        var imageHeight = Math.Abs(imageBox.Height);
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return false;
        }

        // --- Signal 1: the image's placement bbox ≈ the whole page ---
        // Prefer the visible CropBox; fall back to the MediaBox when it is degenerate.
        var pageBox = page.CropBox.Bounds;
        if (Math.Abs(pageBox.Width) <= 0 || Math.Abs(pageBox.Height) <= 0)
        {
            pageBox = page.MediaBox.Bounds;
        }

        var pageWidth = Math.Abs(pageBox.Width);
        var pageHeight = Math.Abs(pageBox.Height);
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return false;
        }

        var widthCoverage = imageWidth / pageWidth;
        var heightCoverage = imageHeight / pageHeight;
        if (widthCoverage < _options.FullPageScanCoverageThreshold ||
            heightCoverage < _options.FullPageScanCoverageThreshold)
        {
            return false;
        }

        // --- Signal 2: the text layer densely covers the image region ---
        // After signal 1 the image ≈ the page, but scope to the image region so the test stays correct when
        // the image is only ~the threshold size. A bare full-page figure/photo has no text here → kept
        // (this is exactly the content #301 exists to capture).
        var regionWords = words.Where(w => CentroidInside(w.BoundingBox, imageBox)).ToList();
        if (regionWords.Count == 0)
        {
            return false;
        }

        // Re-group the in-region words into lines here rather than reusing the per-page `lines` already built
        // in ExtractAsync: passing that list in would force this `protected virtual` method (kept overridable
        // for module extensibility) to expose the internal PdfReadingOrder.TextLine type (CS0051). The
        // re-group runs only for full-page-sized images (≈one per sandwich page) and is cheap relative to the
        // OCR/IO it gates, so the duplicated clustering is an accepted trade-off.
        var regionLines = PdfReadingOrder.GroupWordsIntoLines(regionWords);
        if (regionLines.Count == 0)
        {
            return false;
        }

        // Vertical span of the text block relative to the image height — the PRIMARY guard against dropping
        // a full-page figure whose only text is an edge caption (a caption band spans little of the height).
        var topMost = regionLines.Max(l => l.Bounds.Top);
        var bottomMost = regionLines.Min(l => l.Bounds.Bottom);
        var verticalCoverage = (topMost - bottomMost) / imageHeight;
        if (verticalCoverage < _options.FullPageScanMinTextVerticalCoverage)
        {
            return false;
        }

        // A whole-page transcription is many distributed lines. The invisible-text (Tr 3) ratio is the
        // canonical sandwich signature — sufficient on its own (an OCR layer is invisible by construction)
        // but NOT required, so a visible-but-dense layer still qualifies on line count alone.
        var hasManyLines = regionLines.Count >= _options.FullPageScanMinTextLines;
        var invisibleRatio = InvisibleTextRatio(regionWords);
        var looksInvisibleSandwich = invisibleRatio >= _options.FullPageScanMinInvisibleTextRatio;
        if (!hasManyLines && !looksInvisibleSandwich)
        {
            return false;
        }

        Logger.LogDebug(
            "Skipping a full-page scan background on page {Page}: image covers {Width:P0}×{Height:P0} of the " +
            "page, text spans {Vertical:P0} of the image over {Lines} line(s), invisible-text ratio {Invisible:P0}.",
            page.Number, widthCoverage, heightCoverage, verticalCoverage, regionLines.Count, invisibleRatio);

        return true;
    }

    /// <summary>Whether <paramref name="inner"/>'s centroid falls inside <paramref name="outer"/> (robust to flipped rectangles).</summary>
    private static bool CentroidInside(PdfRectangle inner, PdfRectangle outer)
    {
        var centroid = inner.Centroid;
        var left = Math.Min(outer.Left, outer.Right);
        var right = Math.Max(outer.Left, outer.Right);
        var bottom = Math.Min(outer.Bottom, outer.Top);
        var top = Math.Max(outer.Bottom, outer.Top);
        return centroid.X >= left && centroid.X <= right && centroid.Y >= bottom && centroid.Y <= top;
    }

    /// <summary>
    /// Fraction of letters drawn in the invisible text-rendering mode (<see cref="TextRenderingMode.Neither"/>,
    /// PDF "Tr 3") across the given words — the canonical signature of a scan-sandwich OCR layer. Returns 0
    /// when there are no letters.
    /// </summary>
    private static double InvisibleTextRatio(IReadOnlyList<Word> words)
    {
        long total = 0;
        long invisible = 0;
        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                total++;
                if (letter.RenderingMode == TextRenderingMode.Neither)
                {
                    invisible++;
                }
            }
        }

        return total == 0 ? 0.0 : (double)invisible / total;
    }

    private static TextExtractionResult Empty()
        => new() { Markdown = string.Empty, ProviderName = ProviderIdentifier, UsedOcr = false };

    private static bool HasLetterOrDigit(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(char.IsLetterOrDigit);

    /// <summary>
    /// Resolves the OCR language hints for embedded-image transcription, mirroring
    /// <c>DefaultTextExtractor</c>: the per-document hints when present, otherwise the host's configured
    /// defaults — so the figure path and the whole-page OCR path apply the same defaulting.
    /// </summary>
    protected virtual IList<string> ResolveLanguageHints(TextExtractionContext context)
        => context.LanguageHints?.Count > 0 ? context.LanguageHints : _ocrOptions.DefaultLanguageHints;

    /// <summary>
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing
    /// was lost. This is the single source of truth for both the reason text and completeness
    /// (<c>IsComplete = reason is null</c>), so a new counter cannot drift out of sync with a separate
    /// boolean predicate.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedPages, int pagesWithSkippedImages, int droppedByCap, int undecodable, int truncatedOcr, int failedFigureOcr)
    {
        var parts = new List<string>();
        if (failedPages > 0)
        {
            parts.Add($"{failedPages} page(s) could not be parsed and were skipped");
        }

        if (pagesWithSkippedImages > 0)
        {
            parts.Add($"{pagesWithSkippedImages} page(s) had unreadable images skipped (page text retained)");
        }

        if (undecodable > 0)
        {
            parts.Add($"{undecodable} embedded image(s) could not be decoded to a supported image format");
        }

        if (failedFigureOcr > 0)
        {
            parts.Add($"{failedFigureOcr} embedded image(s) failed OCR (provider error)");
        }

        if (truncatedOcr > 0)
        {
            parts.Add($"{truncatedOcr} image transcription(s) were truncated or discarded by the OCR provider");
        }

        if (droppedByCap > 0)
        {
            parts.Add($"{droppedByCap} image(s) were skipped after reaching the per-document image cap");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private readonly record struct PageContent(Page Page, IReadOnlyList<Word> Words);
}

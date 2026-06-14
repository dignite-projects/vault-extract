using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ocr;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// OpenXML-based Markdown provider for Word documents (#308, Phase 3 of #299). Owns the full <c>.docx</c>
/// parsing pass so it can rebuild the document's structure (headings, and — in later #308 build-order
/// steps — tables, lists, inline formatting, hyperlinks) <b>and</b> extract embedded raster images,
/// transcribe each through the host-selected <see cref="IOcrProvider"/>, and inline the transcription into
/// the Markdown at its reading position. This closes the silent-image-loss gap where embedded figures in
/// Word documents were degraded to a constant <c>![image](embedded-image)</c> placeholder — the exact
/// inline-only mechanism proven by <c>PdfExtractor</c> (#301) and <see cref="PptxExtractor"/> (#307).
/// <para>
/// <b>Image → text uses <see cref="IOcrProvider"/> only</b> (no keyed Vision <c>IChatClient</c> here, no
/// new LLM call site). Semantics are transcription only; the figure's bytes are the OCR input, so there is
/// no user free-text entering a prompt and no <c>PromptBoundary</c> concern. Tables and charts (later step)
/// are pure structured extraction from the OpenXML format (no OCR / no vision / no LLM).
/// </para>
/// <para>
/// <b>Reading order = document order.</b> DOCX is a flow format, so body elements (paragraphs, tables,
/// drawings) are emitted in their XML sequence — no coordinate sort, unlike the fixed-layout PPTX/PDF
/// paths. A floating (<c>wp:anchor</c>) or inline (<c>wp:inline</c>) drawing is processed at the position
/// of its anchoring paragraph, which is what "document order" means for a flow format.
/// </para>
/// <para>
/// <b>Native alt-text</b> (<c>wp:docPr/@descr</c>, fallback <c>@title</c>) is a real caption signal —
/// strictly better than PDF's nearest-text heuristic — and labels the figure block.
/// </para>
/// <para>
/// <b>Tracked changes: accepted view.</b> Inserted-revision text lives in <c>w:t</c> (read normally);
/// deleted-revision text lives in <c>w:delText</c> (LocalName "delText", not "t"). Reading only <c>w:t</c>
/// therefore yields the accepted view of tracked changes for free (#308 decision). Comments and
/// headers/footers are excluded (author-private / page boilerplate); footnotes/endnotes are deferred.
/// </para>
/// <para>
/// <b>Relationship to ElBruno.</b> Unlike <c>.pptx</c>, the catch-all ElBruno provider <i>can</i> convert
/// <c>.docx</c> (it has a Word converter), but only as a <b>module-composition</b> fallback: omit this
/// module and <c>DefaultTextExtractor</c> selects ElBruno for <c>.docx</c>, preserving prior behavior. At
/// <b>runtime</b>, once this module is installed it claims <c>.docx</c> at
/// <see cref="MarkdownProviderPriorities.Specialized"/> and the orchestrator does <b>not</b> fall through
/// to another Markdown provider. So an unopenable <c>.docx</c> is reported as empty +
/// <see cref="TextExtractionResult.IsComplete"/> <c>= false</c> with a reason (#268), the honest escape
/// hatch — it is <b>not</b> silently re-routed to ElBruno at runtime.
/// </para>
/// <para>
/// <b>Current scope (walking skeleton).</b> This step ships headings + flowing paragraph text + embedded
/// raster image transcription end-to-end. Subsequent #308 steps add: tables (<c>w:tbl</c> → Markdown
/// table), lists (<c>w:numPr</c> → bullet/ordered with nesting), inline formatting (bold/italic),
/// hyperlinks, and charts (<c>ChartPart</c> → Markdown table, reusing <see cref="ChartRenderer"/>).
/// </para>
/// </summary>
[ExposeServices(typeof(IMarkdownTextProvider))]
public class DocxExtractor : IMarkdownTextProvider, ITransientDependency
{
    /// <summary>Provider family name surfaced on <see cref="TextExtractionResult.ProviderName"/> for auditability.</summary>
    public const string ProviderIdentifier = "OpenXmlDocx";

    /// <summary>EMU per pixel at 96 DPI (914400 EMU/inch ÷ 96). Used to size images for the decorative threshold.</summary>
    private const long EmuPerPixel96 = 9525;

    /// <summary>
    /// Open settings that collapse OOXML markup-compatibility (<c>mc:AlternateContent</c>) to its single
    /// selected branch <b>before</b> parsing. Under the default <see cref="MarkupCompatibilityProcessMode.NoProcess"/>
    /// both branches stay in the tree, so a <c>Descendants</c> walk would read the SAME content twice: a
    /// modern Word text box stores its text in both the <c>mc:Choice</c> (DrawingML <c>wps:txbx</c>) and the
    /// <c>mc:Fallback</c> (legacy VML that Word auto-writes), and an <c>AlternateContent</c>-wrapped picture
    /// can carry a blip in both branches — duplicating text and double-OCR-ing (and double-charging the image
    /// budget for) one logical figure. <see cref="FileFormatVersions.Office2019"/> is recent enough that the
    /// SDK understands the modern (<c>wps</c> / DrawingML) namespaces, so it keeps the richer Choice branch
    /// (the one whose <c>w:drawing</c> the image path can read) and drops the VML fallback.
    /// </summary>
    private static readonly OpenSettings McCollapsingOpenSettings = new()
    {
        MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
            MarkupCompatibilityProcessMode.ProcessAllParts,
            DocumentFormat.OpenXml.FileFormatVersions.Office2019)
    };

    private readonly IOcrProvider _ocrProvider;
    private readonly OpenXmlExtractorOptions _options;
    private readonly DocumentAIOcrOptions _ocrOptions;

    public ILogger<DocxExtractor> Logger { get; set; } = NullLogger<DocxExtractor>.Instance;

    public DocxExtractor(
        IOcrProvider ocrProvider,
        IOptions<OpenXmlExtractorOptions> options,
        IOptions<DocumentAIOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _options = options.Value;
        _ocrOptions = ocrOptions.Value;
    }

    /// <inheritdoc/>
    public virtual bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".docx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public virtual int Priority => MarkdownProviderPriorities.Specialized;

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        var bytes = await TextExtractionStreams.ReadAllBytesAsync(fileStream, cancellationToken);

        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false, McCollapsingOpenSettings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt / not-actually-a-docx. ElBruno can handle .docx, but only as a module-composition
            // fallback (omit this module → DefaultTextExtractor selects ElBruno). At runtime this provider
            // already owns .docx and the orchestrator does not fall through to another Markdown provider, so
            // returning bare empty would silently drop the document. Report empty + incomplete instead — the
            // honest #268 escape hatch (same stance as PptxExtractor).
            Logger.LogWarning(ex, "Could not open the DOCX ({Bytes} bytes); reporting empty + incomplete.", bytes.Length);
            return new TextExtractionResult
            {
                Markdown = string.Empty,
                ProviderName = ProviderIdentifier,
                UsedOcr = false,
                IsComplete = false,
                IncompleteReason = "The document could not be opened (corrupt or unsupported file)."
            };
        }

        using (document)
        {
            var mainPart = document.MainDocumentPart;
            var body = mainPart?.Document?.Body;

            var state = new ExtractionState
            {
                ImageBudget = _options.MaxImagesPerFile,
                LanguageHints = ResolveLanguageHints(context)
            };

            var blocks = new List<string>();

            if (body is not null)
            {
                foreach (var element in body.ChildElements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await RenderBlockAsync(element, mainPart!, blocks, state, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // OpenXML parses lazily, so a malformed block can fault here on access. Skip it and
                        // mark the result incomplete rather than failing the whole document.
                        Logger.LogWarning(ex, "Failed to process a document block; skipping it.");
                        state.FailedBlocks++;
                    }
                }
            }

            // Single source of truth for the #268 signal: the reason is built from the counters and is
            // null iff nothing was lost; completeness derives from it (no parallel hand-synced predicate).
            var incompleteReason = BuildIncompleteReason(
                state.FailedBlocks, state.DroppedByCap, state.Undecodable, state.OversizedImages,
                state.TruncatedOcr, state.FailedFigureOcr);
            var complete = incompleteReason is null;
            if (!complete)
            {
                Logger.LogWarning("DOCX extraction incomplete: {Reason}", incompleteReason);
            }

            return new TextExtractionResult
            {
                Markdown = string.Join("\n\n", blocks),
                DetectedLanguage = null,
                // UsedOcr means "scan vs digital" (true = physical-scan OCR). A DOCX is a digital extraction
                // even when embedded figures were transcribed via IOcrProvider — figure OCR is auxiliary. Do
                // NOT flip this to true; same contract reasoning as PdfExtractor (#301) / PptxExtractor (#307).
                UsedOcr = false,
                ProviderName = ProviderIdentifier,
                IsComplete = complete,
                IncompleteReason = incompleteReason,
                // DOCX text + per-image OCR has no single aggregated spatial payload to archive (#210).
                NativePayload = null
            };
        }
    }

    /// <summary>
    /// Renders one top-level body element into <paramref name="blocks"/>. This step handles paragraphs
    /// (headings via <see cref="WordStyleMap"/> + flowing text + embedded images). Tables, content-control
    /// (<c>w:sdt</c>) recursion, and chart drawings are added in later #308 build-order steps; until then
    /// non-paragraph elements are skipped.
    /// </summary>
    protected virtual async Task RenderBlockAsync(
        DocumentFormat.OpenXml.OpenXmlElement element,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        switch (element)
        {
            case W.Paragraph paragraph:
                await ProcessParagraphAsync(paragraph, mainPart, blocks, state, cancellationToken);
                break;

            // TODO(#308 later steps): W.Table -> WordTableRenderer; W.SdtBlock -> recurse into content.
        }
    }

    /// <summary>
    /// Emits a paragraph's text block (a heading or flowing text), then transcribes each embedded image in
    /// the paragraph inline after that text — document order for a flow format. Run-level interleaving of
    /// text and images within a single paragraph is deferred; in practice a Word image occupies its own
    /// paragraph (so the text block is empty and only the figure block is emitted).
    /// </summary>
    protected virtual async Task ProcessParagraphAsync(
        W.Paragraph paragraph,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        var text = ParagraphText(paragraph);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var headingLevel = WordStyleMap.HeadingLevel(paragraph);
            if (headingLevel is int level)
            {
                // Collapse internal line breaks so a multi-line heading renders as one clean ATX heading.
                var oneLine = string.Join(
                    " ",
                    text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                blocks.Add(new string('#', level) + " " + oneLine);
            }
            else
            {
                blocks.Add(text);
            }
        }

        foreach (var drawing in paragraph.Descendants<W.Drawing>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HandleDrawingAsync(drawing, mainPart, blocks, state, cancellationToken);
        }
    }

    private async Task HandleDrawingAsync(
        W.Drawing drawing,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        var embed = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
        if (string.IsNullOrEmpty(embed))
        {
            // No image blip: a chart / SmartArt / diagram drawing. Charts are handled in a later #308 step;
            // SmartArt/diagrams are an accepted blind spot. Not counted against completeness.
            return;
        }

        if (IsDecorative(drawing))
        {
            // Icon / bullet / logo / spacer — not figure content, not counted against completeness.
            return;
        }

        if (state.ImageBudget <= 0)
        {
            state.DroppedByCap++;
            return;
        }

        OpenXmlImagePayload.ResolvedImage resolved;
        try
        {
            var part = mainPart.GetPartById(embed);
            resolved = part is ImagePart imagePart
                ? OpenXmlImagePayload.TryResolve(imagePart, _options.MaxImageBytesPerImage)
                // A dangling relationship / non-image part: treat as undecodable.
                : new OpenXmlImagePayload.ResolvedImage(OpenXmlImagePayload.ImageOutcome.Undecodable, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to resolve/decode an embedded DOCX image; skipping it.");
            resolved = new OpenXmlImagePayload.ResolvedImage(OpenXmlImagePayload.ImageOutcome.Undecodable, null, null);
        }

        switch (resolved.Outcome)
        {
            case OpenXmlImagePayload.ImageOutcome.Oversized:
                // A single image larger than the per-image byte cap (e.g. a ZIP-decompression bomb). Skipped
                // before full materialization; trips the completeness signal but never OOMs the worker.
                Logger.LogWarning("Skipped an embedded image exceeding the {Cap}-byte per-image cap.", _options.MaxImageBytesPerImage);
                state.OversizedImages++;
                return;

            case OpenXmlImagePayload.ImageOutcome.Undecodable:
                // Vector (EMF/WMF), dangling relationship, or undecodable/mislabeled bytes.
                state.Undecodable++;
                return;

            case OpenXmlImagePayload.ImageOutcome.Ok:
                break;

            default:
                // A future outcome added to the enum must not silently fall through to RecognizeAsync with
                // possibly-null bytes — fail closed by treating it as undecodable.
                Logger.LogWarning("Unhandled image outcome {Outcome}; treating as undecodable.", resolved.Outcome);
                state.Undecodable++;
                return;
        }

        state.ImageBudget--;

        OcrResult ocr;
        try
        {
            using var imageStream = new MemoryStream(resolved.Bytes!, writable: false);
            ocr = await _ocrProvider.RecognizeAsync(
                imageStream,
                new OcrOptions
                {
                    ContentType = resolved.ContentType!,
                    LanguageHints = state.LanguageHints
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A single figure's OCR failing (provider timeout / rate-limit / auth / one bad image) must NOT
            // discard the document text already extracted — figure OCR is an auxiliary augmentation, not the
            // document's primary payload (the #210/#268 "auxiliary step must not break the main pipeline"
            // principle). Skip this figure and mark the result incomplete. OperationCanceledException still
            // propagates so a host/job shutdown aborts promptly.
            Logger.LogWarning(ex, "Embedded-image OCR failed; keeping the document text, skipping this figure.");
            state.FailedFigureOcr++;
            return;
        }

        if (!ocr.IsComplete)
        {
            // OCR truncated at the token limit or discarded by the repetition guard.
            state.TruncatedOcr++;
        }

        var transcription = ocr.Markdown?.Trim() ?? string.Empty;
        if (transcription.Length == 0)
        {
            return;
        }

        // Native alt-text (wp:docPr/@descr, fallback @title) is a real caption signal. Alt-text is
        // author-controlled free text (often multi-line), so collapse newlines via MarkdownCell.Inline so
        // the bold caption can't break the OCR block (often a table) directly below it.
        var caption = AltTextOf(drawing);
        var markdown = string.IsNullOrWhiteSpace(caption)
            ? transcription
            : "**" + MarkdownCell.Inline(caption) + "**\n\n" + transcription;

        blocks.Add(markdown);
    }

    /// <summary>Whether the drawing's display size is below the decorative threshold (skipped silently).</summary>
    protected virtual bool IsDecorative(W.Drawing drawing)
    {
        var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
        if (extent?.Cx is null || extent.Cy is null)
        {
            // No display extents: cannot judge — do not skip.
            return false;
        }

        // Compute the area before dividing so neither dimension is truncated toward zero first (which would
        // shrink a borderline figure below the threshold and drop it). EMU values are well within long range
        // for any real document, so the product cannot overflow.
        var pixelArea = extent.Cx.Value * extent.Cy.Value / (EmuPerPixel96 * EmuPerPixel96);
        return pixelArea < _options.MinImagePixels;
    }

    /// <summary>
    /// Concatenates a paragraph's visible run text in document order: run text (<c>w:t</c>), tabs
    /// (<c>w:tab</c> → space) and line breaks (<c>w:br</c>/<c>w:cr</c> → newline). Deleted-revision text
    /// lives in <c>w:delText</c> (LocalName "delText", not "t"), so reading only <c>w:t</c> yields the
    /// accepted view of tracked changes for free (#308 decision). Concatenating only <c>w:t</c> without the
    /// break handling would silently fuse the two sides of a line break.
    /// </summary>
    protected static string ParagraphText(W.Paragraph paragraph)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var element in paragraph.Descendants<DocumentFormat.OpenXml.OpenXmlElement>())
        {
            switch (element.LocalName)
            {
                case "t":
                    sb.Append(element.InnerText);
                    break;
                case "tab":
                    sb.Append(' ');
                    break;
                case "br":
                case "cr":
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string? AltTextOf(W.Drawing drawing)
    {
        var props = drawing.Descendants<DW.DocProperties>().FirstOrDefault();
        var description = props?.Description?.Value;
        return !string.IsNullOrWhiteSpace(description) ? description : props?.Title?.Value;
    }

    /// <summary>
    /// Resolves the OCR language hints for embedded-image transcription, mirroring <c>DefaultTextExtractor</c>
    /// and <see cref="PptxExtractor"/>: the per-document hints when present, otherwise the host's configured
    /// defaults — so the figure path and the whole-page OCR path apply the same defaulting.
    /// </summary>
    protected virtual IList<string> ResolveLanguageHints(TextExtractionContext context)
        => context.LanguageHints?.Count > 0 ? context.LanguageHints : _ocrOptions.DefaultLanguageHints;

    /// <summary>
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing was
    /// lost. Single source of truth for both the reason text and completeness (<c>IsComplete = reason is
    /// null</c>), so a new counter cannot drift out of sync. Extended with chart loss causes as the chart
    /// path lands in a later #308 build-order step.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedBlocks, int droppedByCap, int undecodable, int oversizedImages, int truncatedOcr, int failedFigureOcr)
    {
        var parts = new List<string>();
        if (failedBlocks > 0)
        {
            parts.Add($"{failedBlocks} document block(s) could not be parsed and were skipped");
        }

        if (undecodable > 0)
        {
            parts.Add($"{undecodable} embedded image(s) could not be decoded to a supported raster format (e.g. EMF/WMF vector)");
        }

        if (oversizedImages > 0)
        {
            parts.Add($"{oversizedImages} embedded image(s) exceeded the per-image size cap and were skipped");
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
            parts.Add($"{droppedByCap} image(s) were skipped after reaching the per-file image cap");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    /// <summary>Mutable per-extraction accumulator: image budget and #268 loss counters.</summary>
    protected sealed class ExtractionState
    {
        public int ImageBudget;
        public int FailedBlocks;
        public int DroppedByCap;
        public int Undecodable;
        public int OversizedImages;
        public int TruncatedOcr;
        public int FailedFigureOcr;
        public IList<string> LanguageHints = new List<string>();
    }
}

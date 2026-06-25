using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using D = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using P = DocumentFormat.OpenXml.Presentation;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// OpenXML-based Markdown provider for PowerPoint decks (#307, Phase 2 of #299). Owns the full
/// <c>.pptx</c> parsing pass so it can extract slide text (with native heading/table structure and
/// speaker notes) <b>and</b> the embedded raster images, transcribe each image through the host-selected
/// <see cref="IOcrProvider"/>, render <c>ChartPart</c> backing data as Markdown tables, and inline
/// everything into the Markdown at its slide/shape reading position. This closes the silent-image-loss
/// gap where embedded figures in decks never reached the channel output — the exact inline-only
/// mechanism proven by <c>PdfExtractor</c> in #301.
/// <para>
/// <b>Image → text uses <see cref="IOcrProvider"/> only</b> (no keyed Vision <c>IChatClient</c> here, no
/// new LLM call site). Semantics are transcription only; the figure's bytes are the OCR input, so there
/// is no user free-text entering a prompt and no <c>PromptBoundary</c> concern. Charts and tables are
/// pure structured extraction from the OpenXML format (no OCR / no vision / no LLM).
/// </para>
/// <para>
/// <b>Reading order.</b> PPTX is semantically fixed-layout per slide, so blocks are ordered by shape
/// offset (top-to-bottom, then left-to-right) within slide order — PDF-like, not flow-based.
/// </para>
/// <para>
/// <b>Difference from <c>PdfExtractor</c>.</b> A PDF with no text layer returns empty so the
/// orchestrator's whole-page OCR fallback owns it. There is <b>no</b> such fallback for PPTX
/// (it is PDF-only in <c>DefaultTextExtractor</c>), and once this module is installed it owns
/// <c>.pptx</c> outright. So an unopenable deck does not silently return empty Markdown — it returns
/// empty + <see cref="TextExtractionResult.IsComplete"/> <c>= false</c> with a reason (#268), the honest
/// escape hatch.
/// </para>
/// <para>
/// <b>This module is required for <c>.pptx</c> text.</b> The catch-all ElBruno provider does <b>not</b>
/// support <c>.pptx</c> (its converter set covers PDF/Word/HTML/CSV/RTF/EPUB but not PresentationML), so
/// omitting this module makes <c>.pptx</c> fall through to ElBruno, which returns empty Markdown — and
/// because <c>.pptx</c> is not <c>.pdf</c>, the orchestrator's whole-page OCR fallback does not fire
/// either. So this is <b>not</b> a "graceful degradation preserving prior behavior": there was no prior
/// <c>.pptx</c> capability; this module is what gives <c>.pptx</c> any text at all.
/// </para>
/// </summary>
[ExposeServices(typeof(IMarkdownTextProvider))]
public class PptxExtractor : IMarkdownTextProvider, ITransientDependency
{
    /// <summary>Provider family name surfaced on <see cref="TextExtractionResult.ProviderName"/> for auditability.</summary>
    public const string ProviderIdentifier = "OpenXmlPptx";

    /// <summary>EMU per pixel at 96 DPI (914400 EMU/inch ÷ 96). Used to size images for the decorative threshold.</summary>
    private const long EmuPerPixel96 = 9525;

    private readonly IOcrProvider _ocrProvider;
    private readonly OpenXmlExtractorOptions _options;
    private readonly ExtractOcrOptions _ocrOptions;

    public ILogger<PptxExtractor> Logger { get; set; } = NullLogger<PptxExtractor>.Instance;

    public PptxExtractor(
        IOcrProvider ocrProvider,
        IOptions<OpenXmlExtractorOptions> options,
        IOptions<ExtractOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _options = options.Value;
        _ocrOptions = ocrOptions.Value;
    }

    /// <inheritdoc/>
    public virtual bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".pptx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public virtual int Priority => MarkdownProviderPriorities.Specialized;

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ParseStreams.ReadAllBytesAsync(fileStream, cancellationToken);

        PresentationDocument document;
        try
        {
            document = PresentationDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt / not-actually-a-pptx. There is NO whole-page OCR fallback for PPTX (PDF-only), and
            // this provider already owns .pptx, so returning bare empty would silently drop the document.
            // Report empty + incomplete instead — the honest #268 escape hatch.
            Logger.LogWarning(ex, "Could not open the PPTX ({Bytes} bytes); reporting empty + incomplete.", bytes.Length);
            return new TextExtractionResult
            {
                Markdown = string.Empty,
                ProviderName = ProviderIdentifier,
                UsedOcr = false,
                IsComplete = false,
                IncompleteReason = "The presentation could not be opened (corrupt or unsupported file)."
            };
        }

        using (document)
        {
            var presentation = document.PresentationPart?.Presentation;
            var slideIds = presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? new List<P.SlideId>();

            var state = new ExtractionState
            {
                ImageBudget = _options.MaxImagesPerFile,
                LanguageHints = ResolveLanguageHints(context)
            };

            var slideMarkdowns = new List<string>(slideIds.Count);

            foreach (var slideId in slideIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                {
                    continue;
                }

                SlidePart slidePart;
                try
                {
                    slidePart = (SlidePart)document.PresentationPart!.GetPartById(relId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(ex, "Could not resolve a slide part; skipping the slide.");
                    state.FailedSlides++;
                    continue;
                }

                string slideMarkdown;
                try
                {
                    slideMarkdown = await ProcessSlideAsync(slidePart, state, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // OpenXML parses lazily, so a malformed slide can fault here on access. Skip it and
                    // mark the result incomplete rather than failing the whole deck.
                    Logger.LogWarning(ex, "Failed to process a slide; skipping it.");
                    state.FailedSlides++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(slideMarkdown))
                {
                    slideMarkdowns.Add(slideMarkdown);
                }
            }

            // Single source of truth for the #268 signal: the reason is built from the counters and is
            // null iff nothing was lost; completeness derives from it (no parallel hand-synced predicate).
            var incompleteReason = BuildIncompleteReason(
                state.FailedSlides, state.DroppedByCap, state.Undecodable, state.OversizedImages,
                state.TruncatedOcr, state.FailedFigureOcr, state.ChartFailures);
            var complete = incompleteReason is null;
            if (!complete)
            {
                Logger.LogWarning("PPTX extraction incomplete: {Reason}", incompleteReason);
            }

            return new TextExtractionResult
            {
                Markdown = string.Join("\n\n", slideMarkdowns),
                DetectedLanguage = null,
                // UsedOcr means "scan vs digital" (true = physical-scan OCR). A PPTX is a digital
                // extraction even when embedded figures were transcribed via IOcrProvider — figure OCR is
                // auxiliary. Do NOT flip this to true; same contract reasoning as PdfExtractor (#301).
                UsedOcr = false,
                ProviderName = ProviderIdentifier,
                IsComplete = complete,
                IncompleteReason = incompleteReason,
                // PPTX text + per-image OCR has no single aggregated spatial payload to archive (#210).
                NativePayload = null
            };
        }
    }

    /// <summary>Builds one slide's Markdown: ordered shape blocks, then the optional speaker-notes block.</summary>
    private async Task<string> ProcessSlideAsync(
        SlidePart slidePart, ExtractionState state, CancellationToken cancellationToken)
    {
        var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        var blocks = new List<SlideReadingOrder.SlideBlock>();

        if (shapeTree is not null)
        {
            await WalkShapesAsync(shapeTree, slidePart, inGroup: false, groupY: 0, groupX: 0, blocks, state, cancellationToken);
        }

        var body = SlideReadingOrder.Render(blocks);

        if (_options.IncludeSpeakerNotes)
        {
            var notes = ReadSpeakerNotes(slidePart);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                var notesBlock = "### Speaker notes\n\n" + notes;
                body = string.IsNullOrWhiteSpace(body) ? notesBlock : body + "\n\n" + notesBlock;
            }
        }

        return body;
    }

    /// <summary>
    /// Walks a shape container (the slide's shape tree or a grouped shape) in document order, dispatching
    /// pictures to OCR, charts/tables to structured rendering, and text shapes to Markdown. Recurses into
    /// grouped shapes; for items inside a group, the group's own offset anchors their reading position and
    /// document-encounter order (<see cref="ExtractionState.Sequence"/>) breaks ties within the group —
    /// avoiding the child-coordinate-space math a precise absolute position would require.
    /// </summary>
    private async Task WalkShapesAsync(
        DocumentFormat.OpenXml.OpenXmlElement container,
        SlidePart slidePart,
        bool inGroup,
        long groupY,
        long groupX,
        List<SlideReadingOrder.SlideBlock> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (child)
            {
                case P.GroupShape group:
                {
                    var (gy, gx) = inGroup ? (groupY, groupX) : OffsetOf(group);
                    await WalkShapesAsync(group, slidePart, inGroup: true, gy, gx, blocks, state, cancellationToken);
                    break;
                }

                case P.Picture picture:
                {
                    var (y, x) = PositionFor(picture, inGroup, groupY, groupX);
                    await HandlePictureAsync(picture, slidePart, y, x, blocks, state, cancellationToken);
                    break;
                }

                case P.GraphicFrame graphicFrame:
                {
                    var (y, x) = PositionFor(graphicFrame, inGroup, groupY, groupX);
                    HandleGraphicFrame(graphicFrame, slidePart, y, x, blocks, state);
                    break;
                }

                case P.Shape shape:
                {
                    var (y, x) = PositionFor(shape, inGroup, groupY, groupX);
                    HandleTextShape(shape, y, x, blocks, state);
                    break;
                }

                // ConnectionShape / non-visual property elements / unknown: nothing to extract.
            }
        }
    }

    private async Task HandlePictureAsync(
        P.Picture picture,
        SlidePart slidePart,
        long y,
        long x,
        List<SlideReadingOrder.SlideBlock> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        if (IsDecorative(picture))
        {
            // Icon / bullet / logo / spacer — not figure content, not counted against completeness.
            return;
        }

        var embed = picture.BlipFill?.Blip?.Embed?.Value;
        if (string.IsNullOrEmpty(embed))
        {
            // A picture shape with no image relationship (e.g. an unfilled placeholder). Nothing to OCR.
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
            var part = slidePart.GetPartById(embed);
            resolved = part is ImagePart imagePart
                ? OpenXmlImagePayload.TryResolve(imagePart, _options.MaxImageBytesPerImage)
                // A dangling relationship / non-image part: treat as undecodable.
                : new OpenXmlImagePayload.ResolvedImage(OpenXmlImagePayload.ImageOutcome.Undecodable, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to resolve/decode an embedded PPTX image; skipping it.");
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
            // A single figure's OCR failing (provider timeout / rate-limit / auth / one bad image) must
            // NOT discard the slide text already extracted — figure OCR is an auxiliary augmentation, not
            // the deck's primary payload (the #210/#268 "auxiliary step must not break the main pipeline"
            // principle). Skip this figure and mark the result incomplete. OperationCanceledException
            // still propagates so a host/job shutdown aborts promptly.
            Logger.LogWarning(ex, "Embedded-image OCR failed; keeping the slide text, skipping this figure.");
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

        // Native alt-text (p:cNvPr/@descr, fallback @title) is a real caption signal — strictly better
        // than PDF's nearest-text heuristic. Render it as the figure block's label. Alt-text is
        // author-controlled free text (often multi-line), so collapse newlines via MarkdownText.InlineLabel
        // AND inline-escape via MarkdownText.EscapeInline so the bold caption can't break the OCR block (often
        // a table) below it, nor inject a link/emphasis from a literal [..](..)/*.
        var caption = AltTextOf(picture);
        var markdown = string.IsNullOrWhiteSpace(caption)
            ? transcription
            : "**" + MarkdownText.EscapeInline(MarkdownText.InlineLabel(caption)) + "**\n\n" + transcription;

        blocks.Add(new SlideReadingOrder.SlideBlock(y, x, state.Sequence++, markdown));
    }

    private void HandleGraphicFrame(
        P.GraphicFrame graphicFrame,
        SlidePart slidePart,
        long y,
        long x,
        List<SlideReadingOrder.SlideBlock> blocks,
        ExtractionState state)
    {
        var graphicData = graphicFrame.Graphic?.GraphicData;
        if (graphicData is null)
        {
            return;
        }

        // Chart: ChartPart backing data → Markdown table (pure structured, no OCR/vision/LLM).
        var chartReference = graphicData.Descendants<C.ChartReference>().FirstOrDefault();
        if (!string.IsNullOrEmpty(chartReference?.Id?.Value))
        {
            string? table;
            try
            {
                var part = slidePart.GetPartById(chartReference!.Id!.Value!);
                table = part is ChartPart chartPart ? ChartRenderer.Render(chartPart) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Failed to render an embedded chart; skipping it.");
                table = null;
            }

            if (string.IsNullOrWhiteSpace(table))
            {
                // Unsupported chart family (scatter/bubble) or unreadable cache — count it as lost (#268).
                state.ChartFailures++;
                return;
            }

            blocks.Add(new SlideReadingOrder.SlideBlock(y, x, state.Sequence++, table!));
            return;
        }

        // Native table (a:tbl): real text content that would otherwise be lost, since this provider owns
        // the whole .pptx pass. Render directly as a Markdown table (no OCR).
        var drawingTable = graphicData.Descendants<D.Table>().FirstOrDefault();
        if (drawingTable is not null)
        {
            var rendered = DrawingTableRenderer.Render(drawingTable);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                blocks.Add(new SlideReadingOrder.SlideBlock(y, x, state.Sequence++, rendered!));
            }

            return;
        }

        // SmartArt / diagram / embedded OLE: vector-rendered, no faithful text extraction this round —
        // accepted blind spot like vector graphics, NOT counted against completeness (#307 decision).
    }

    private void HandleTextShape(
        P.Shape shape,
        long y,
        long x,
        List<SlideReadingOrder.SlideBlock> blocks,
        ExtractionState state)
    {
        // Skip auto-generated slide-number / date fields so they do not pollute the text payload — the
        // same exclusion ReadSpeakerNotes applies to the notes slide (a slide-number placeholder's cached
        // value would otherwise leak as a stray "1" / date into the body Markdown).
        if (IsAutoFieldPlaceholder(shape))
        {
            return;
        }

        var text = ShapeText(shape);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Title placeholders are a real structural signal — render as a Markdown heading. Collapse any
        // internal line/paragraph breaks to single spaces so a multi-paragraph title is one clean heading.
        if (IsTitle(shape))
        {
            text = "## " + string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        blocks.Add(new SlideReadingOrder.SlideBlock(y, x, state.Sequence++, text));
    }

    /// <summary>Whether the shape is an auto-generated slide-number / date placeholder (excluded from text).</summary>
    private static bool IsAutoFieldPlaceholder(P.Shape shape)
    {
        var type = PlaceholderTypeOf(shape);
        return type == P.PlaceholderValues.SlideNumber || type == P.PlaceholderValues.DateAndTime;
    }

    private static P.PlaceholderValues? PlaceholderTypeOf(P.Shape shape)
        => shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;

    /// <summary>Whether the picture's display size is below the decorative threshold (skipped silently).</summary>
    protected virtual bool IsDecorative(P.Picture picture)
    {
        var extents = picture.ShapeProperties?.Transform2D?.Extents;
        if (extents?.Cx is null || extents.Cy is null)
        {
            // No display extents (e.g. inherited from layout): cannot judge — do not skip.
            return false;
        }

        // Compute the area before dividing so neither dimension is truncated toward zero first (which
        // would shrink a borderline figure below the threshold and drop it). EMU values are well within
        // long range for any real slide, so the product cannot overflow.
        var pixelArea = extents.Cx.Value * extents.Cy.Value / (EmuPerPixel96 * EmuPerPixel96);
        return pixelArea < _options.MinImagePixels;
    }

    /// <summary>Reads a slide's speaker notes, excluding the slide-number / date placeholders.</summary>
    protected virtual string? ReadSpeakerNotes(SlidePart slidePart)
    {
        var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
        if (notesSlide is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var shape in notesSlide.Descendants<P.Shape>())
        {
            if (IsAutoFieldPlaceholder(shape))
            {
                continue;
            }

            var text = ShapeText(shape);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static (long Y, long X) PositionFor(
        DocumentFormat.OpenXml.OpenXmlElement shape, bool inGroup, long groupY, long groupX)
        => inGroup ? (groupY, groupX) : OffsetOf(shape);

    /// <summary>The shape's own top-left offset in EMU, or (0,0) when it inherits position from the layout.</summary>
    private static (long Y, long X) OffsetOf(DocumentFormat.OpenXml.OpenXmlElement shape)
    {
        var offset = shape switch
        {
            P.Picture p => p.ShapeProperties?.Transform2D?.Offset,
            P.Shape s => s.ShapeProperties?.Transform2D?.Offset,
            P.GraphicFrame g => g.Transform?.Offset,
            P.GroupShape grp => grp.GroupShapeProperties?.TransformGroup?.Offset,
            _ => null
        };

        // A shape with no explicit xfrm inherits its position from the slide layout/master; we cannot
        // resolve that here, so it sorts to the top by position and keeps document order via Sequence.
        return offset is null ? (0, 0) : (offset.Y?.Value ?? 0, offset.X?.Value ?? 0);
    }

    private static string ShapeText(P.Shape shape)
    {
        var body = shape.TextBody;
        if (body is null)
        {
            return string.Empty;
        }

        // Each paragraph is verbatim slide text emitted into Markdown, so escape it: a line beginning with
        // "# "/"- "/"> "/"1." or containing "*"/"[" must not be re-parsed as a heading / list / link. A Title
        // placeholder is escaped here too, then gets its generated "## " prefix in HandleTextShape.
        var paragraphs = body
            .Elements<D.Paragraph>()
            .Select(p => MarkdownText.EscapeBlockText(ParagraphText(p)))
            .Where(line => line.Length > 0);

        // Join paragraphs with a blank line so each stays a distinct Markdown paragraph. A single newline
        // would fold consecutive bullets/lines into one rendered paragraph downstream, losing the list
        // structure that the text-extraction rules call a real signal.
        return string.Join("\n\n", paragraphs);
    }

    private static string ParagraphText(D.Paragraph paragraph)
    {
        // Walk the paragraph in document order, emitting run text (a:t, including a:fld field text) and
        // turning a soft line break (a:br) into a newline. Concatenating only a:t would silently fuse the
        // two sides of a break (e.g. "123 Main St" + "Suite 400" → "123 Main StSuite 400").
        var sb = new System.Text.StringBuilder();
        foreach (var element in paragraph.Descendants<DocumentFormat.OpenXml.OpenXmlElement>())
        {
            switch (element.LocalName)
            {
                case "t":
                    sb.Append(element.InnerText);
                    break;
                case "br":
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string? AltTextOf(P.Picture picture)
    {
        var props = picture.NonVisualPictureProperties?.NonVisualDrawingProperties;
        var description = props?.Description?.Value;
        return !string.IsNullOrWhiteSpace(description) ? description : props?.Title?.Value;
    }

    private static bool IsTitle(P.Shape shape)
    {
        var type = PlaceholderTypeOf(shape);
        return type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle;
    }

    /// <summary>
    /// Resolves the OCR language hints for embedded-image transcription, mirroring
    /// <c>DefaultTextExtractor</c>: the per-document hints when present, otherwise the host's configured
    /// defaults — so the figure path and the whole-page OCR path apply the same defaulting.
    /// </summary>
    protected virtual IList<string> ResolveLanguageHints(TextExtractionContext context)
        => context.LanguageHints?.Count > 0 ? context.LanguageHints : _ocrOptions.DefaultLanguageHints;

    /// <summary>
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing
    /// was lost. Single source of truth for both the reason text and completeness
    /// (<c>IsComplete = reason is null</c>), so a new counter cannot drift out of sync.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedSlides, int droppedByCap, int undecodable, int oversizedImages, int truncatedOcr, int failedFigureOcr, int chartFailures)
    {
        var parts = new List<string>();
        if (failedSlides > 0)
        {
            parts.Add($"{failedSlides} slide(s) could not be parsed and were skipped");
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

        if (chartFailures > 0)
        {
            parts.Add($"{chartFailures} chart(s) could not be rendered as a table");
        }

        if (droppedByCap > 0)
        {
            parts.Add($"{droppedByCap} image(s) were skipped after reaching the per-file image cap");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    /// <summary>Mutable per-extraction accumulator: image budget, block sequence, and #268 loss counters.</summary>
    private sealed class ExtractionState
    {
        public int ImageBudget;
        public int Sequence;
        public int FailedSlides;
        public int DroppedByCap;
        public int Undecodable;
        public int OversizedImages;
        public int TruncatedOcr;
        public int FailedFigureOcr;
        public int ChartFailures;
        public IList<string> LanguageHints = new List<string>();
    }
}

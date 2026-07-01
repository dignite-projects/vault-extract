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

    public ILogger<PptxExtractor> Logger { get; set; } = NullLogger<PptxExtractor>.Instance;

    public PptxExtractor(
        IOcrProvider ocrProvider,
        IOptions<OpenXmlExtractorOptions> options)
    {
        _ocrProvider = ocrProvider;
        _options = options.Value;
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

            var state = new PptxExtractionState
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
                    state.FailedContainers++;
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
                    state.FailedContainers++;
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
                state.FailedContainers, state.DroppedByCap, state.Undecodable, state.OversizedImages,
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
        SlidePart slidePart, PptxExtractionState state, CancellationToken cancellationToken)
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
    /// document-encounter order (<see cref="PptxExtractionState.Sequence"/>) breaks ties within the group —
    /// avoiding the child-coordinate-space math a precise absolute position would require.
    /// </summary>
    private async Task WalkShapesAsync(
        DocumentFormat.OpenXml.OpenXmlElement container,
        SlidePart slidePart,
        bool inGroup,
        long groupY,
        long groupX,
        List<SlideReadingOrder.SlideBlock> blocks,
        PptxExtractionState state,
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
        PptxExtractionState state,
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

        // Native alt-text (p:cNvPr/@descr, fallback @title) is a real caption signal — strictly better than
        // PDF's nearest-text heuristic. The shared figure pipeline (budget → resolve → outcome switch → OCR →
        // truncation → caption, #317) returns the finished block; PPTX sinks it as a positioned SlideBlock so
        // reading-order sorting can place it by shape offset (state.Sequence breaks ties in document order).
        var block = await OpenXmlFigureTranscriber.TranscribeAsync(
            slidePart, embed, AltTextOf(picture), state, _options, _ocrProvider, Logger, cancellationToken);
        if (block is not null)
        {
            blocks.Add(new SlideReadingOrder.SlideBlock(y, x, state.Sequence++, block));
        }
    }

    private void HandleGraphicFrame(
        P.GraphicFrame graphicFrame,
        SlidePart slidePart,
        long y,
        long x,
        List<SlideReadingOrder.SlideBlock> blocks,
        PptxExtractionState state)
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
        PptxExtractionState state)
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
    /// Resolves the OCR language hints for embedded-image transcription: the per-document hints from the
    /// context, or empty. There is no central host default (#441 removed it); a provider that needs a
    /// language default reads its own config (e.g. PaddleOcr:Languages). Kept <c>protected virtual</c> so a
    /// consumer can override to supply hints (e.g. from per-tenant config).
    /// </summary>
    protected virtual IList<string> ResolveLanguageHints(TextExtractionContext context)
        => OpenXmlExtractionState.ResolveLanguageHints(context);

    /// <summary>
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing
    /// was lost. Single source of truth for both the reason text and completeness
    /// (<c>IsComplete = reason is null</c>), so a new counter cannot drift out of sync.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedSlides, int droppedByCap, int undecodable, int oversizedImages, int truncatedOcr, int failedFigureOcr, int chartFailures)
        => OpenXmlIncompleteReason.Build(
            "slide", failedSlides, droppedByCap, undecodable, oversizedImages, truncatedOcr, failedFigureOcr, chartFailures);

    /// <summary>
    /// PPTX reading-order extends the shared <see cref="OpenXmlExtractionState"/> with a monotonic
    /// document-encounter counter: PPTX is fixed-layout, so blocks are sorted by shape offset and
    /// <see cref="Sequence"/> breaks ties in document order (DOCX emits in flow order and needs no such
    /// field). All the image-budget / #268 loss counters are inherited (#317).
    /// </summary>
    private sealed class PptxExtractionState : OpenXmlExtractionState
    {
        public int Sequence;
    }
}

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
using C = DocumentFormat.OpenXml.Drawing.Charts;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// OpenXML-based Markdown provider for Word documents (#308, Phase 3 of #299). Owns the full <c>.docx</c>
/// parsing pass so it can rebuild the document's structure (headings, tables, charts, inline formatting,
/// hyperlinks, lists) <b>and</b> extract embedded raster images,
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
/// <b>Current scope.</b> Headings, flowing paragraph text with inline formatting (bold/italic) and
/// hyperlinks, lists (<c>w:numPr</c> → bullet/ordered with nesting), text boxes (<c>w:txbxContent</c> →
/// text block), block-level content controls (<c>w:sdt</c>) and custom-XML wrappers (recursed), embedded
/// raster image transcription (DrawingML <c>a:blip</c> and legacy VML <c>v:imagedata</c>), tables
/// (<c>w:tbl</c> → Markdown table, with in-cell figures extracted after the table), and charts
/// (<c>ChartPart</c> backing data → Markdown table, reusing <see cref="ChartRenderer"/>). This completes the
/// structural rebuild for #308.
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
    /// Spaces of indentation per list-nesting level (w:ilvl). Must be at least the width of the widest
    /// marker we emit (<c>"1. "</c> = 3) so a child under an <b>ordered</b> parent is still recognized as
    /// nested by CommonMark — 2 spaces would make it a sibling and even split the ordered list, silently
    /// corrupting both the hierarchy and the ordinal numbering. 3 also covers bullets (<c>"- "</c> = 2).
    /// </summary>
    private const int IndentSpacesPerListLevel = 3;

    /// <summary>
    /// Hard cap on content-control / custom-XML block nesting depth walked recursively. Pathologically deep
    /// (or malformed) nesting would otherwise risk a <see cref="System.StackOverflowException"/> — which is
    /// uncatchable and would kill the worker. Past this depth the subtree is skipped and counted against the
    /// #268 signal. Real documents nest only a handful of levels.
    /// </summary>
    private const int MaxBlockNestingDepth = 32;

    /// <summary>
    /// Hard cap on list nesting depth (w:ilvl) applied when indenting a Markdown list item. Word supports at
    /// most nine levels; a malformed / attacker w:ilvl would otherwise allocate a huge indent string (or
    /// overflow on <c>level * IndentSpacesPerListLevel</c>).
    /// </summary>
    private const int MaxListIndentLevels = 9;

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
                        await RenderBlockAsync(element, mainPart!, blocks, state, depth: 0, cancellationToken);
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
                state.TruncatedOcr, state.FailedFigureOcr, state.ChartFailures);
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
    /// Renders one top-level body element into <paramref name="blocks"/>. Handles paragraphs (headings via
    /// <see cref="WordStyleMap"/> + flowing text + embedded images) and tables
    /// (<see cref="WordTableRenderer"/>). Block-level content controls (<c>w:sdt</c>) and custom-XML wrappers
    /// recurse into their content; charts and images ride the paragraph's / table's drawings. A non-paragraph
    /// block that still carries visible text trips the #268 signal rather than being silently dropped.
    /// </summary>
    protected virtual async Task RenderBlockAsync(
        DocumentFormat.OpenXml.OpenXmlElement element,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        int depth,
        CancellationToken cancellationToken)
    {
        switch (element)
        {
            case W.Paragraph paragraph:
                await ProcessParagraphAsync(paragraph, mainPart, blocks, state, cancellationToken);
                break;

            case W.Table table:
            {
                // Native table -> Markdown table (pure structured extraction, no OCR). A null/empty render
                // (layout-only or empty grid) is simply not added; a parse fault is caught by the caller's
                // per-block try/catch and tripped as FailedBlocks.
                var renderedTable = WordTableRenderer.Render(table);
                if (!string.IsNullOrWhiteSpace(renderedTable))
                {
                    blocks.Add(renderedTable!);
                }

                // A Markdown table cell can't host a multi-line transcription / chart block, so a figure
                // inside a cell is extracted as its own block AFTER the table — content preserved (over its
                // exact in-cell position), a failure still trips #268, rather than being silently dropped.
                await ExtractContainerFiguresAsync(table, mainPart, blocks, state, cancellationToken);
                break;
            }

            case W.SdtBlock sdt:
                // Block-level content control (a form / template field wrapping paragraphs, tables, etc.).
                // Recurse into its content so the wrapped body is not silently dropped.
                await RenderChildBlocksAsync(sdt.SdtContentBlock, mainPart, blocks, state, depth, cancellationToken);
                break;

            case W.CustomXmlBlock customXml:
                // Block-level custom-XML wrapper around body content — recurse the same way.
                await RenderChildBlocksAsync(customXml, mainPart, blocks, state, depth, cancellationToken);
                break;

            default:
                // sectPr / bookmark / proofErr and similar markers carry no body text and are correctly
                // skipped. But if an unhandled block carries visible text, do NOT drop it silently — trip the
                // #268 signal so downstream knows content was lost.
                if (!string.IsNullOrWhiteSpace(element.InnerText))
                {
                    Logger.LogWarning("Skipping an unsupported body block <{Name}> that carries text.", element.LocalName);
                    state.FailedBlocks++;
                }

                break;
        }
    }

    /// <summary>
    /// Emits a paragraph's text block — a heading (plain collapsed text) or a body paragraph rendered with
    /// inline formatting (bold/italic) and hyperlinks via <see cref="WordParagraphRenderer"/> — then
    /// transcribes each embedded image in the paragraph inline after that text (document order for a flow
    /// format). Run-level interleaving of text and images within a single paragraph is deferred; in practice
    /// a Word image occupies its own paragraph (so the text block is empty and only the figure block is
    /// emitted).
    /// </summary>
    protected virtual async Task ProcessParagraphAsync(
        W.Paragraph paragraph,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        var headingLevel = WordStyleMap.HeadingLevel(paragraph);
        if (headingLevel is int level)
        {
            // Headings render as plain collapsed text (no inline emphasis markup inside an ATX heading).
            var text = ParagraphText(paragraph);
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Collapse internal line breaks so a multi-line heading renders as one clean ATX heading.
                var oneLine = string.Join(
                    " ",
                    text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                blocks.Add(new string('#', level) + " " + oneLine);
            }
        }
        else
        {
            // Body paragraphs render with inline formatting (bold/italic) and hyperlinks.
            var markdown = WordParagraphRenderer.Render(paragraph, mainPart);
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                // A list item (w:numPr) gets a Markdown bullet / ordered marker, indented by nesting level.
                var list = WordListNumbering.Resolve(paragraph, mainPart.NumberingDefinitionsPart);
                if (list is { } listInfo)
                {
                    // Clamp the nesting level: a malformed / attacker w:ilvl would otherwise allocate a huge
                    // indent string (level * 3) or overflow. Word supports at most nine list levels.
                    var clampedLevel = Math.Clamp(listInfo.Level, 0, MaxListIndentLevels);
                    var indent = new string(' ', clampedLevel * IndentSpacesPerListLevel);
                    markdown = indent + (listInfo.Ordered ? "1. " : "- ") + markdown;
                }

                blocks.Add(markdown);
            }
        }

        // Text boxes (a DrawingML wps:txbx inside a w:drawing, or a VML v:textbox inside a w:pict) -> a text
        // block. Matched by LocalName "txbxContent" (not a strong type) so it works whichever
        // markup-compatibility branch survived collapsing — the SDK leaves a VML fallback's descendants as
        // untyped elements, which a strong-typed Descendants would miss. Only top-level text boxes are
        // processed (a text box nested inside another is folded into its parent's block) to avoid emitting
        // the nested text twice. MC collapsing on open normally drops the VML fallback; as a
        // belt-and-suspenders guard (a malformed package, or a future relaxation of the open settings) we
        // also skip a text box whose nearest mc:AlternateContent fork already contributed a sibling branch,
        // so a text box is never emitted twice regardless of MC processing.
        var seenForks = new HashSet<DocumentFormat.OpenXml.OpenXmlElement>();
        foreach (var textBox in paragraph.Descendants()
                     .Where(e => e.LocalName == "txbxContent" && !HasTextBoxAncestor(e)))
        {
            var fork = textBox.Ancestors().FirstOrDefault(a => a.LocalName == "AlternateContent");
            if (fork is not null && !seenForks.Add(fork))
            {
                continue;
            }

            var textBoxMarkdown = TextBoxText(textBox);
            if (!string.IsNullOrWhiteSpace(textBoxMarkdown))
            {
                blocks.Add(textBoxMarkdown);
            }
        }

        await ExtractContainerFiguresAsync(paragraph, mainPart, blocks, state, cancellationToken);
    }

    /// <summary>
    /// Transcribes a container's (paragraph or table) embedded images and renders its chart drawings.
    /// Covers both DrawingML images (<c>a:blip</c> in <c>w:drawing</c>) and legacy VML raster images
    /// (<c>v:imagedata</c> in <c>w:pict</c>), so a compatibility-mode / legacy DOCX does not silently drop
    /// figures. Used for both body paragraphs and table cells (a Markdown cell can't host a transcription).
    /// </summary>
    private async Task ExtractContainerFiguresAsync(
        DocumentFormat.OpenXml.OpenXmlElement container,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        // Skip drawings nested inside a text box (txbxContent): the text box's own (outer) drawing is
        // processed once and its recursive blip lookup transcribes the inner image a single time. Without
        // this skip the recursive Descendants walk visits BOTH the outer text-box drawing AND the nested
        // image drawing and OCRs the same image twice (double cost + double budget + duplicate block).
        foreach (var drawing in container.Descendants<W.Drawing>().Where(d => !HasTextBoxAncestor(d)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HandleDrawingAsync(drawing, mainPart, blocks, state, cancellationToken);
        }

        // Legacy VML raster images (w:pict/v:imagedata) are not W.Drawing, so the DrawingML walk above
        // misses them. They reference PNG/JPEG bytes via an r:id (or o:relid) relationship — transcribe them
        // too rather than silently dropping the figure. (A vector-only VML shape has no imagedata relationship.)
        // Known limitation: VML images are NOT decorative-size-filtered (a VML shape's size lives in its style
        // attribute, not wp:extent), so a tiny VML icon may be transcribed where its DrawingML equivalent
        // would be skipped by IsDecorative. VML in modern DOCX is rare, so this is accepted (see #318-area).
        foreach (var imageData in container.Descendants()
                     .Where(e => e.LocalName == "imagedata" && !HasTextBoxAncestor(e)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationshipId = VmlImageRelationshipId(imageData);
            if (!string.IsNullOrEmpty(relationshipId))
            {
                await TranscribeEmbeddedImageAsync(relationshipId!, caption: null, mainPart, blocks, state, cancellationToken);
            }
        }
    }

    /// <summary>Recursively renders the block-level children of a content-control / custom-XML wrapper.</summary>
    private async Task RenderChildBlocksAsync(
        DocumentFormat.OpenXml.OpenXmlElement? container,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        int depth,
        CancellationToken cancellationToken)
    {
        if (container is null)
        {
            return;
        }

        if (depth >= MaxBlockNestingDepth)
        {
            // Pathological / malformed content-control nesting — stop recursing rather than risk an
            // (uncatchable) StackOverflowException that would kill the worker. Count the dropped subtree (#268).
            Logger.LogWarning("Content-control nesting exceeded depth {Max}; skipping the remaining subtree.", MaxBlockNestingDepth);
            if (!string.IsNullOrWhiteSpace(container.InnerText))
            {
                state.FailedBlocks++;
            }

            return;
        }

        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RenderBlockAsync(child, mainPart, blocks, state, depth + 1, cancellationToken);
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
            // No image blip: a chart (render its backing data as a table) or SmartArt/diagram/OLE (an
            // accepted blind spot). Charts go through the format-agnostic ChartRenderer — no OCR / vision.
            HandleChart(drawing, mainPart, blocks, state);
            return;
        }

        if (IsDecorative(drawing))
        {
            // Icon / bullet / logo / spacer — not figure content, not counted against completeness.
            return;
        }

        // Native alt-text (wp:docPr/@descr, fallback @title) is a real caption signal — strictly better than
        // PDF's nearest-text heuristic.
        await TranscribeEmbeddedImageAsync(embed, AltTextOf(drawing), mainPart, blocks, state, cancellationToken);
    }

    /// <summary>
    /// Resolves an embedded image relationship to its bytes and transcribes it via the host-selected
    /// <see cref="IOcrProvider"/>, appending the transcription (optionally captioned) as a block. Shared by
    /// the DrawingML (<c>a:blip</c>) and legacy VML (<c>v:imagedata</c>) image paths. Honors the per-file
    /// image budget and trips the #268 counters on cap / oversize / undecodable / OCR-failure / truncation.
    /// </summary>
    private async Task TranscribeEmbeddedImageAsync(
        string relationshipId,
        string? caption,
        MainDocumentPart mainPart,
        List<string> blocks,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        if (state.ImageBudget <= 0)
        {
            state.DroppedByCap++;
            return;
        }

        OpenXmlImagePayload.ResolvedImage resolved;
        try
        {
            var part = mainPart.GetPartById(relationshipId);
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

        // Caption (alt-text) is author-controlled free text (often multi-line), so collapse newlines via
        // MarkdownCell.Inline so the bold caption can't break the OCR block (often a table) directly below it.
        var markdown = string.IsNullOrWhiteSpace(caption)
            ? transcription
            : "**" + MarkdownCell.Inline(caption) + "**\n\n" + transcription;

        blocks.Add(markdown);
    }

    /// <summary>
    /// The embedded-image relationship id of a VML <c>v:imagedata</c> element: <c>r:id</c> (the
    /// officeDocument relationships namespace) or, as a fallback, <c>o:relid</c>. Read by attribute (not a
    /// strong type) because VML descendants are often left untyped by the SDK.
    /// </summary>
    private static string? VmlImageRelationshipId(DocumentFormat.OpenXml.OpenXmlElement imageData)
    {
        foreach (var attribute in imageData.GetAttributes())
        {
            if (attribute.LocalName == "id" && attribute.NamespaceUri.Contains("relationships"))
            {
                return attribute.Value;
            }
        }

        foreach (var attribute in imageData.GetAttributes())
        {
            if (attribute.LocalName == "relid")
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders an embedded chart's backing data as a Markdown table via the format-agnostic
    /// <see cref="ChartRenderer"/> (pure structured extraction — no OCR / vision / LLM). A chart with no
    /// renderable category/value cache (e.g. scatter/bubble) or an unreadable part trips the completeness
    /// signal (#268); a non-chart drawing with no blip (SmartArt / diagram / OLE) is an accepted blind spot.
    /// </summary>
    private void HandleChart(W.Drawing drawing, MainDocumentPart mainPart, List<string> blocks, ExtractionState state)
    {
        var chartId = drawing.Descendants<C.ChartReference>().FirstOrDefault()?.Id?.Value;
        if (string.IsNullOrEmpty(chartId))
        {
            // SmartArt / diagram / OLE object: accepted blind spot, not counted against completeness.
            return;
        }

        string? table;
        try
        {
            var part = mainPart.GetPartById(chartId);
            table = part is ChartPart chartPart ? ChartRenderer.Render(chartPart) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to render an embedded chart; skipping it.");
            table = null;
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            // Unsupported chart family (scatter/bubble) or an unreadable cache — count it as lost (#268).
            state.ChartFailures++;
            return;
        }

        blocks.Add(table!);
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

        var cx = extent.Cx.Value;
        var cy = extent.Cy.Value;

        // A degenerate (zero/negative) or attacker-large extent is not a small decorative image, so don't
        // skip it — and bounding the dimensions here keeps the area multiplication below long-overflow (real
        // extents are far under the bound, so the area math below stays exact).
        const long MaxSaneExtentEmu = 100L * 914400; // 100 inches — larger than any real page dimension
        if (cx <= 0 || cy <= 0 || cx > MaxSaneExtentEmu || cy > MaxSaneExtentEmu)
        {
            return false;
        }

        // Compute the area before dividing so neither dimension is truncated toward zero first (which would
        // shrink a borderline figure below the threshold and drop it).
        var pixelArea = cx * cy / (EmuPerPixel96 * EmuPerPixel96);
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
            // A text box's content is emitted as its own block by the txbxContent loop in
            // ProcessParagraphAsync, so it must not also be folded into a heading's plain text here —
            // otherwise a heading that anchors a text box both absorbs that text (gluing it onto the heading
            // line) and duplicates it as a separate block.
            if (HasTextBoxAncestor(element))
            {
                continue;
            }

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

    private static bool HasTextBoxAncestor(DocumentFormat.OpenXml.OpenXmlElement element)
        => element.Ancestors().Any(a => a.LocalName == "txbxContent");

    /// <summary>
    /// Extracts a text box's text by LocalName (so it works for both the DrawingML <c>wps:txbx</c> and the
    /// untyped VML fallback): <c>w:t</c> → text, <c>w:tab</c> → space, <c>w:br</c>/<c>w:cr</c> → newline,
    /// <c>w:p</c> → paragraph break. <c>w:delText</c> is not matched, so deleted-revision text is excluded
    /// (the accepted view). Inline emphasis is not applied inside a text box this step.
    /// </summary>
    private static string TextBoxText(DocumentFormat.OpenXml.OpenXmlElement textBoxContent)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var element in textBoxContent.Descendants())
        {
            switch (element.LocalName)
            {
                case "p":
                    sb.Append("\n\n");
                    break;
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
        int failedBlocks, int droppedByCap, int undecodable, int oversizedImages, int truncatedOcr, int failedFigureOcr, int chartFailures)
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
        public int ChartFailures;
        public IList<string> LanguageHints = new List<string>();
    }
}

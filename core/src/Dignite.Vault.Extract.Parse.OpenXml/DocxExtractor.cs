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
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

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
/// headers/footers are excluded (author-private / page boilerplate); footnotes/endnotes are surfaced as
/// Markdown-footnote definitions at the end of the document (#315).
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

    private readonly IOcrProvider _ocrProvider;
    private readonly OpenXmlExtractorOptions _options;

    public ILogger<DocxExtractor> Logger { get; set; } = NullLogger<DocxExtractor>.Instance;

    public DocxExtractor(
        IOcrProvider ocrProvider,
        IOptions<OpenXmlExtractorOptions> options)
    {
        _ocrProvider = ocrProvider;
        _options = options.Value;
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
        var bytes = await ParseStreams.ReadAllBytesAsync(fileStream, cancellationToken);

        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false, OpenXmlPackageSettings.McCollapsing);
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

            var state = new DocxExtractionState
            {
                ImageBudget = _options.MaxImagesPerFile,
                LanguageHints = ResolveLanguageHints(context)
            };

            // Build the per-document hyperlink id -> uri cache once (#318) so a link resolves in O(1) rather
            // than scanning HyperlinkRelationships per hyperlink during the body render.
            state.HyperlinkUris = mainPart is null ? null : BuildHyperlinkUris(mainPart);

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
                        state.FailedContainers++;
                    }
                }
            }

            // Footnotes / endnotes: markers were emitted inline during the body walk (WordParagraphRenderer
            // collected each reference); resolve their bodies from the notes parts and append them as a
            // Markdown-footnote-style notes section at the end of the document (#315).
            await AppendNotesAsync(mainPart, blocks, state, cancellationToken);

            // Single source of truth for the #268 signal: the reason is built from the counters and is
            // null iff nothing was lost; completeness derives from it (no parallel hand-synced predicate).
            var incompleteReason = BuildIncompleteReason(
                state.FailedContainers, state.DroppedByCap, state.Undecodable, state.OversizedImages,
                state.TruncatedOcr, state.FailedFigureOcr, state.ChartFailures, state.FailedNotes);
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
        DocxExtractionState state,
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
                    // per-block try/catch and tripped as FailedContainers. The note collector is threaded in so
                    // a footnote/endnote reference inside a cell is captured, not silently dropped (#315).
                    var renderedTable = WordTableRenderer.Render(table, state.NoteReferences);
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
                    state.FailedContainers++;
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
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        var headingLevel = WordStyleMap.HeadingLevel(paragraph, mainPart, state.StyleHeadingCache);
        if (headingLevel is int level)
        {
            // Headings render as plain collapsed text (no inline emphasis markup inside an ATX heading).
            var text = ParagraphText(paragraph);
            // A footnote/endnote reference inside a heading is real content. The plain-text heading path does
            // not go through WordParagraphRenderer (which collects markers for body paragraphs), so collect
            // the references here and append their markers — otherwise the marker and the note body were
            // silently dropped and the loss bypassed the #268 signal (#315).
            var noteMarkers = CollectNoteMarkers(paragraph, state.NoteReferences);
            if (!string.IsNullOrWhiteSpace(text) || noteMarkers.Length > 0)
            {
                // Collapse internal line breaks so a multi-line heading renders as one clean ATX heading.
                var oneLine = string.Join(
                    " ",
                    text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                // Escape the heading text so a literal "#"/"[...]"/"*" in it does not extend the ATX run or
                // inject a link/emphasis (the "# " prefix we add is generated structure, kept intact). Note
                // markers are generated structure too, so they are appended past the escape.
                blocks.Add(new string('#', level) + " " + MarkdownText.EscapeBlockText(oneLine) + noteMarkers);
            }
        }
        else
        {
            // Body paragraphs render with inline formatting (bold/italic) and hyperlinks.
            var markdown = WordParagraphRenderer.Render(paragraph, mainPart, state.NoteReferences, state.HyperlinkUris);
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                // A list item (w:numPr) gets a Markdown bullet / ordered marker, indented by nesting level.
                var list = WordListNumbering.Resolve(paragraph, mainPart.NumberingDefinitionsPart, state.NumberingFormatCache);
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
    /// Transcribes a container's (paragraph, table cell, or note) embedded images and renders its chart
    /// drawings. Images are walked as <b>picture instances</b> (<c>pic:pic</c>) rather than <c>w:drawing</c>
    /// elements (#322), so a grouped drawing's several pictures are each transcribed once, a text-box image is
    /// transcribed once with its OWN extent / caption (read from the picture's nearest <c>wp:inline</c> /
    /// <c>wp:anchor</c>), and the previous grouped-multi-image loss and double-OCR special-case both go away.
    /// Charts ride the <c>c:chart</c> reference on a <c>w:drawing</c>; legacy VML raster images ride
    /// <c>v:imagedata</c> — neither is a <c>pic:pic</c>, so each keeps its own pass.
    /// </summary>
    private async Task ExtractContainerFiguresAsync(
        DocumentFormat.OpenXml.OpenXmlElement container,
        MainDocumentPart mainPart,
        List<string> blocks,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        // One subtree traversal (#318): collect the pictures, chart drawings, and legacy VML images in a
        // single Descendants() pass, then emit in the established order (pictures, then charts, then VML) so
        // the Markdown output is byte-for-byte unchanged. A pic:pic is transcribed regardless of a text-box
        // ancestor (#322 — the text-box image is real content), while a chart / VML inside a text box stays
        // skipped (an accepted blind spot). Known VML limitation: not decorative-size-filtered (its size lives
        // in a style attribute, not wp:extent), so a tiny VML icon may be transcribed; VML in modern DOCX is
        // rare, so this is accepted.
        var pictures = new List<Pic.Picture>();
        var fillDrawings = new List<W.Drawing>();
        var chartDrawings = new List<W.Drawing>();
        var vmlImages = new List<DocumentFormat.OpenXml.OpenXmlElement>();

        foreach (var element in container.Descendants())
        {
            switch (element)
            {
                case Pic.Picture picture:
                    pictures.Add(picture);
                    break;

                case W.Drawing drawing when !HasTextBoxAncestor(drawing):
                    // A drawing that contains a pic:pic is already covered by the Pic.Picture case above. One
                    // WITHOUT a pic:pic but WITH an a:blip is a shape/group whose fill is a picture
                    // (wps:wsp/a:blipFill/a:blip) — real image content the picture walk misses, so transcribe
                    // it rather than dropping it silently (#322 fill-image regression). Anything else is a
                    // chart (c:chart) or a SmartArt/diagram/OLE blind spot.
                    if (!drawing.Descendants<Pic.Picture>().Any())
                    {
                        if (drawing.Descendants<A.Blip>().Any())
                        {
                            fillDrawings.Add(drawing);
                        }
                        else
                        {
                            chartDrawings.Add(drawing);
                        }
                    }

                    break;

                default:
                    if (element.LocalName == "imagedata" && !HasTextBoxAncestor(element))
                    {
                        vmlImages.Add(element);
                    }

                    break;
            }
        }

        // Images: one transcription per picture instance, with metadata from the picture's own inline/anchor.
        foreach (var picture in pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HandlePictureAsync(picture, mainPart, blocks, state, cancellationToken);
        }

        // Shape/group picture fills (a:blipFill on a wps:wsp / wpg, no pic:pic): transcribe via the shape's
        // own extent / alt-text. Emitted after the pictures; these were previously dropped entirely, so no
        // prior output ordering is disturbed (#322).
        foreach (var drawing in fillDrawings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HandleFillDrawingAsync(drawing, mainPart, blocks, state, cancellationToken);
        }

        // Charts: a w:drawing referencing a c:chart renders as a table; HandleChart self-filters non-charts.
        foreach (var drawing in chartDrawings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HandleChart(drawing, mainPart, blocks, state);
        }

        // Legacy VML raster images (v:imagedata), not pic:pic — transcribe via their r:id relationship.
        foreach (var imageData in vmlImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationshipId = VmlImageRelationshipId(imageData);
            if (!string.IsNullOrEmpty(relationshipId))
            {
                await TranscribeEmbeddedImageAsync(relationshipId!, caption: null, mainPart, blocks, state, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Resolves the footnote / endnote references collected during the body walk (#315) and appends each note
    /// body as a Markdown-footnote definition (<c>[^fn{id}]: body</c>) at the end of the document, in
    /// first-reference order and de-duplicated (a note referenced twice is defined once). The auto-inserted
    /// separator / continuation notes are excluded (no author content). A reference that cannot be resolved —
    /// a dangling id, or a missing FootnotesPart / EndnotesPart — trips the #268 signal via
    /// <see cref="DocxExtractionState.FailedNotes"/> rather than being silently dropped.
    /// </summary>
    private async Task AppendNotesAsync(
        MainDocumentPart? mainPart,
        List<string> blocks,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        if (mainPart is null || state.NoteReferences.Count == 0)
        {
            return;
        }

        var footnotes = BuildNoteBodyMap(mainPart.FootnotesPart?.Footnotes?.Elements<W.Footnote>());
        var endnotes = BuildNoteBodyMap(mainPart.EndnotesPart?.Endnotes?.Elements<W.Endnote>());

        // #457: a hyperlink inside a note body is a relationship of the FootnotesPart / EndnotesPart, not the
        // main part, so it must be resolved against the owning part's own id -> uri map. Built once per part,
        // mirroring the body-path cache (#318). A missing part yields no map here, but that pairs with a null
        // body map above (the note is then unresolvable), so a resolved note always has its part's map in hand.
        var footnoteHyperlinkUris = mainPart.FootnotesPart is { } footnotesPart ? BuildHyperlinkUris(footnotesPart) : null;
        var endnoteHyperlinkUris = mainPart.EndnotesPart is { } endnotesPart ? BuildHyperlinkUris(endnotesPart) : null;

        var seen = new HashSet<NoteReference>();
        foreach (var reference in state.NoteReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A note referenced more than once is defined a single time.
            if (!seen.Add(reference))
            {
                continue;
            }

            var isFootnote = reference.Kind == NoteKind.Footnote;
            var map = isFootnote ? footnotes : endnotes;
            if (map is null || !map.TryGetValue(reference.Id, out var note))
            {
                // Dangling reference, or the notes part is missing entirely — surface the loss (#268).
                Logger.LogWarning(
                    "A {Kind} reference (id {Id}) could not be resolved to a note body.", reference.Kind, reference.Id);
                state.FailedNotes++;
                continue;
            }

            var hyperlinkUris = isFootnote ? footnoteHyperlinkUris : endnoteHyperlinkUris;
            var body = await RenderNoteBodyAsync(note, mainPart, hyperlinkUris, state, cancellationToken);
            blocks.Add(FormatNoteDefinition(reference.Marker, body));
        }
    }

    /// <summary>
    /// Formats one Markdown-footnote definition: the label, then the body with every line AFTER the first
    /// indented four spaces (blank separators kept blank). The indent is required by the Markdown footnote
    /// syntax (Pandoc / cmark-gfm) so a multi-paragraph or figure note body stays attached to the definition;
    /// without it the second paragraph is flush-left and escapes as an ordinary document-level paragraph (and
    /// can wedge between two definitions), corrupting the output structure (#315).
    /// </summary>
    private static string FormatNoteDefinition(string marker, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"{marker}:";
        }

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.Append(marker).Append(": ").Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            // Keep a blank separator blank; indent every content continuation line so it attaches to the note.
            if (lines[i].Length > 0)
            {
                sb.Append("    ").Append(lines[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Indexes footnote / endnote definitions by <c>w:id</c>, excluding the auto-inserted
    /// <c>separator</c> / <c>continuationSeparator</c> notes (they carry no author content). Returns
    /// <c>null</c> when the part is absent, so the caller distinguishes "no notes part" (a reference is
    /// therefore dangling) from an empty part.
    /// </summary>
    private static IReadOnlyDictionary<long, W.FootnoteEndnoteType>? BuildNoteBodyMap(
        IEnumerable<W.FootnoteEndnoteType>? notes)
    {
        if (notes is null)
        {
            return null;
        }

        var map = new Dictionary<long, W.FootnoteEndnoteType>();
        foreach (var note in notes)
        {
            if (note.Id?.Value is not { } id)
            {
                continue;
            }

            var type = note.Type?.Value;
            if (type == W.FootnoteEndnoteValues.Separator || type == W.FootnoteEndnoteValues.ContinuationSeparator)
            {
                continue;
            }

            map[id] = note;
        }

        return map;
    }

    /// <summary>
    /// Renders a footnote / endnote body to Markdown by reusing the body paragraph/run renderer (so a note
    /// that carries formatting / links is handled consistently) and transcribing any embedded image in the
    /// note through the shared <see cref="OpenXmlFigureTranscriber"/> (#315 red line: notes go through the
    /// host <see cref="IOcrProvider"/>, no new LLM call site). Paragraphs are joined with blank lines. A
    /// nested note reference inside a note is not followed (no collector is passed), so it does not recurse.
    /// </summary>
    private async Task<string> RenderNoteBodyAsync(
        W.FootnoteEndnoteType note,
        MainDocumentPart mainPart,
        IReadOnlyDictionary<string, string?>? hyperlinkUris,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        foreach (var paragraph in note.Elements<W.Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // #457: resolve note-body hyperlinks against the owning notes part's relationships (threaded in),
            // not the main document's — the two parts have independent r:id spaces.
            var text = WordParagraphRenderer.Render(paragraph, mainPart, hyperlinkUris: hyperlinkUris);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }

            // A figure inside a note (rare) is transcribed via the same host OCR path, appended after its text.
            await ExtractContainerFiguresAsync(paragraph, mainPart, parts, state, cancellationToken);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Recursively renders the block-level children of a content-control / custom-XML wrapper.</summary>
    private async Task RenderChildBlocksAsync(
        DocumentFormat.OpenXml.OpenXmlElement? container,
        MainDocumentPart mainPart,
        List<string> blocks,
        DocxExtractionState state,
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
                state.FailedContainers++;
            }

            return;
        }

        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RenderBlockAsync(child, mainPart, blocks, state, depth + 1, cancellationToken);
        }
    }

    /// <summary>
    /// Transcribes one embedded picture (<c>pic:pic</c>) via the host OCR path, reading its decorative-size
    /// threshold and caption from the picture's own <c>wp:inline</c> / <c>wp:anchor</c> ancestor so a grouped
    /// or text-box image uses its OWN extent / alt-text rather than the group's / text box's (#322).
    /// </summary>
    private async Task HandlePictureAsync(
        Pic.Picture picture,
        MainDocumentPart mainPart,
        List<string> blocks,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        var embed = picture.BlipFill?.Blip?.Embed?.Value;
        if (string.IsNullOrEmpty(embed))
        {
            // A picture with no embedded blip (e.g. a linked / external image) — nothing to transcribe.
            return;
        }

        if (IsDecorative(picture))
        {
            // Icon / bullet / logo / spacer — not figure content, not counted against completeness.
            return;
        }

        // Native alt-text (wp:docPr/@descr, fallback @title) is a real caption signal — strictly better than
        // PDF's nearest-text heuristic.
        await TranscribeEmbeddedImageAsync(embed, AltTextOf(picture), mainPart, blocks, state, cancellationToken);
    }

    /// <summary>
    /// Transcribes a shape / group picture-fill drawing — a <c>wps:wsp</c> / <c>wpg</c> whose fill is an
    /// <c>a:blipFill</c> with no <c>pic:pic</c> for the picture walk to catch. Judges the decorative-size
    /// filter on the drawing's OWN <c>wp:extent</c> and reads its caption from the drawing's <c>wp:docPr</c>.
    /// Restores the pre-#322 blip-anywhere behavior for fill images that the pic:pic walk otherwise dropped
    /// silently (#322).
    /// </summary>
    private async Task HandleFillDrawingAsync(
        W.Drawing drawing,
        MainDocumentPart mainPart,
        List<string> blocks,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        var embed = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
        if (string.IsNullOrEmpty(embed))
        {
            return;
        }

        // No pic:pic, so IsDecorative(Pic.Picture) does not apply — judge the drawing's own wp:extent (the
        // outermost inline/anchor extent; no extent => cannot judge => keep).
        if (IsDecorativeExtent(drawing.Descendants<DW.Extent>().FirstOrDefault()))
        {
            return;
        }

        var caption = CaptionFromDocProperties(drawing.Descendants<DW.DocProperties>().FirstOrDefault());
        await TranscribeEmbeddedImageAsync(embed, caption, mainPart, blocks, state, cancellationToken);
    }

    /// <summary>
    /// Resolves an embedded image relationship and transcribes it via the host-selected
    /// <see cref="IOcrProvider"/>, appending the (optionally captioned) transcription as a block. Shared by
    /// the DrawingML (<c>a:blip</c>) and legacy VML (<c>v:imagedata</c>) image paths. The figure-OCR pipeline
    /// itself — budget guard, resolve, image-outcome switch, OCR, truncation signal, caption — lives in the
    /// shared <see cref="OpenXmlFigureTranscriber"/> (#317, shared with <see cref="PptxExtractor"/>); this
    /// wrapper only supplies the DOCX part container + caption and sinks the finished block in flow order.
    /// </summary>
    private async Task TranscribeEmbeddedImageAsync(
        string relationshipId,
        string? caption,
        MainDocumentPart mainPart,
        List<string> blocks,
        DocxExtractionState state,
        CancellationToken cancellationToken)
    {
        // DOCX is a flow document — no page concept — so the figure marker is the bare page-less form (#480).
        var block = await OpenXmlFigureTranscriber.TranscribeAsync(
            mainPart, relationshipId, caption, pageNumber: null, state, _options, _ocrProvider, Logger, cancellationToken);
        if (block is not null)
        {
            blocks.Add(block);
        }
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
    private void HandleChart(W.Drawing drawing, MainDocumentPart mainPart, List<string> blocks, DocxExtractionState state)
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

    /// <summary>
    /// Whether the picture's display size is below the decorative threshold (skipped silently). The extent is
    /// read from the picture's nearest <c>wp:inline</c> / <c>wp:anchor</c> ancestor (#322), so a text-box
    /// image is judged by its own size rather than the text box's.
    /// </summary>
    protected virtual bool IsDecorative(Pic.Picture picture)
        => IsDecorativeExtent(NearestDrawingExtent(picture));

    /// <summary>
    /// Whether a <c>wp:extent</c> is below the decorative threshold (skipped silently). Shared by the picture
    /// path (its nearest inline/anchor extent) and the shape-fill path (the drawing's own extent, #322).
    /// </summary>
    private bool IsDecorativeExtent(DW.Extent? extent)
    {
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

    /// <summary>The nearest inline (<c>wp:inline</c>) or floating (<c>wp:anchor</c>) drawing-wrapper ancestor
    /// of the picture, or <c>null</c> — the source of the picture's own extent + docPr (#322). For a text-box
    /// image this is the inner drawing (the image's), not the outer text-box drawing.</summary>
    private static DocumentFormat.OpenXml.OpenXmlElement? NearestDrawingWrapper(Pic.Picture picture)
        => picture.Ancestors().FirstOrDefault(a => a is DW.Inline or DW.Anchor);

    /// <summary>The <c>wp:extent</c> of the picture's nearest inline / floating drawing ancestor, or <c>null</c>.</summary>
    private static DW.Extent? NearestDrawingExtent(Pic.Picture picture)
        => NearestDrawingWrapper(picture)?.GetFirstChild<DW.Extent>();

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

    /// <summary>
    /// Appends a Markdown marker (<c>[^fn{id}]</c> / <c>[^en{id}]</c>) for each footnote/endnote reference in
    /// the paragraph, in reading order, and records the reference on <paramref name="sink"/> so its body is
    /// resolved and defined at the document end (#315). Used by the plain-text heading path, which — unlike
    /// the body path via <see cref="WordParagraphRenderer"/> — would otherwise drop the reference silently.
    /// Text-box references are excluded (a text box is emitted as its own block).
    /// </summary>
    private static string CollectNoteMarkers(W.Paragraph paragraph, ICollection<NoteReference> sink)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var element in paragraph.Descendants<DocumentFormat.OpenXml.OpenXmlElement>())
        {
            if (HasTextBoxAncestor(element))
            {
                continue;
            }

            var reference = element switch
            {
                W.FootnoteReference fn when fn.Id?.Value is { } fnId => new NoteReference(NoteKind.Footnote, fnId),
                W.EndnoteReference en when en.Id?.Value is { } enId => new NoteReference(NoteKind.Endnote, enId),
                _ => (NoteReference?)null
            };

            if (reference is { } note)
            {
                sb.Append(note.Marker);
                sink.Add(note);
            }
        }

        return sb.ToString();
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

        // Text-box text is verbatim document content emitted as its own block, so escape it like a paragraph:
        // a line that begins with "# "/"- "/"> " or contains "*"/"[" must not be re-parsed as structure.
        return MarkdownText.EscapeBlockText(sb.ToString().Trim());
    }

    private static string? AltTextOf(Pic.Picture picture)
        => CaptionFromDocProperties(NearestDocProperties(picture));

    /// <summary>The caption from a <c>wp:docPr</c>: its description (<c>@descr</c>), or its title as a fallback.
    /// Shared by the picture path (nearest docPr) and the shape-fill path (the drawing's own docPr, #322).</summary>
    private static string? CaptionFromDocProperties(DW.DocProperties? props)
    {
        var description = props?.Description?.Value;
        return !string.IsNullOrWhiteSpace(description) ? description : props?.Title?.Value;
    }

    /// <summary>The <c>wp:docPr</c> of the picture's nearest inline / floating drawing ancestor — the image's
    /// own caption source, not the group's / text box's (#322).</summary>
    private static DW.DocProperties? NearestDocProperties(Pic.Picture picture)
        => NearestDrawingWrapper(picture)?.GetFirstChild<DW.DocProperties>();

    /// <summary>
    /// Builds a part's hyperlink relationship-id → URI map once (#318 for the main part; #457 for a
    /// FootnotesPart / EndnotesPart, whose note-body links live in a separate relationship space). Relationship
    /// ids are unique within a part, so a plain dictionary suffices; a relationship with no target maps to
    /// <c>null</c> (an internal anchor).
    /// </summary>
    private static IReadOnlyDictionary<string, string?> BuildHyperlinkUris(OpenXmlPart part)
    {
        var map = new Dictionary<string, string?>();
        foreach (var relationship in part.HyperlinkRelationships)
        {
            map[relationship.Id] = relationship.Uri?.ToString();
        }

        return map;
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
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing was
    /// lost. Single source of truth for both the reason text and completeness (<c>IsComplete = reason is
    /// null</c>), so a new counter cannot drift out of sync. Extended with chart loss causes as the chart
    /// path lands in a later #308 build-order step.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedBlocks, int droppedByCap, int undecodable, int oversizedImages, int truncatedOcr, int failedFigureOcr, int chartFailures, int failedNotes = 0)
        => OpenXmlIncompleteReason.Build(
            "document block", failedBlocks, droppedByCap, undecodable, oversizedImages, truncatedOcr, failedFigureOcr, chartFailures, failedNotes);

    /// <summary>
    /// DOCX-specific per-extraction accumulator: extends the shared <see cref="OpenXmlExtractionState"/> with
    /// the footnote / endnote references collected during the body walk (in reading order) and the count of
    /// references that could not be resolved to a note body (#315).
    /// </summary>
    protected sealed class DocxExtractionState : OpenXmlExtractionState
    {
        /// <summary>Footnote / endnote references seen in the body, in reading order (#315).</summary>
        internal List<NoteReference> NoteReferences { get; } = new();

        /// <summary>References whose note body could not be resolved — dangling id / missing notes part (#268).</summary>
        public int FailedNotes;

        /// <summary>Per-document hyperlink relationship-id → URI cache, built once (#318); null until built.</summary>
        public IReadOnlyDictionary<string, string?>? HyperlinkUris;

        /// <summary>Per-document <c>(numId, level) → numbering-format</c> memo, populated lazily (#318).</summary>
        public Dictionary<(int NumId, int Level), W.NumberFormatValues?> NumberingFormatCache { get; } = new();

        /// <summary>Per-document custom-style <c>styleId → heading-level</c> memo, populated lazily (#458). Spares
        /// every styled body paragraph a fresh <c>w:basedOn</c>-chain walk over styles.xml.</summary>
        public Dictionary<string, int?> StyleHeadingCache { get; } = new();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Extract.Abstractions.Parse;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using DlaTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;

namespace Dignite.Extract.Parse.Pdf;

/// <summary>
/// Layout reconstruction for a single PDF page: segments the text layer into column/region blocks,
/// orders the blocks into reading order, groups each block into visual lines, interleaves
/// embedded-figure transcriptions at their reading position, and (optionally) binds a figure to its
/// nearest caption line.
/// <para>
/// PDF user space has a bottom-left origin (Y increases upward), so "top of the page" = the highest
/// <see cref="PdfRectangle.Top"/>.
/// </para>
/// <para>
/// <b>Band-aware reading order (#310 Phase A).</b> <see cref="RenderPage"/> segments the page with PdfPig's
/// <c>DocumentLayoutAnalysis</c> (<see cref="RecursiveXYCut"/>), then orders blocks band-aware
/// (<see cref="SegmentIntoReadingOrder"/>): horizontal bands at full-width separators, visual rows within a
/// band. A multi-column label/body row no longer splices a left-column label into the middle of a right-column
/// sentence (the failure mode of a flat <c>Top</c>-descending sort), and single-column prose interleaved with a
/// table reads top-to-bottom. Within a single block the order is still <c>Top</c> descending then <c>Left</c>
/// ascending (see <see cref="Render"/>), which is correct for one column.
/// </para>
/// <para>
/// <b>Table row reconstruction (#310 Phase B).</b> On a multi-block page <see cref="RenderPage"/> also
/// re-aggregates neighbouring blocks (<see cref="RecursiveXYCut"/> typically cuts a table along its column
/// gutters and row gaps into per-cell blocks) and, when they confidently form a 2-D grid, renders the region
/// as a <b>Markdown table</b> via <see cref="PdfTableReconstruction"/> so a wrapped cell stays in its own
/// cell instead of interleaving with its row siblings. A region that does not read as a table degrades to
/// the paragraph path. The flat path remains a non-lossy fallback when segmentation finds a single region or
/// faults.
/// </para>
/// <para>
/// Caption association is <b>placement/labeling only</b>: a caption is never sent into the OCR call.
/// </para>
/// </summary>
internal static class PdfReadingOrder
{
    /// <summary>An embedded image with its page placement, source page number, and the OCR transcription of its content.</summary>
    public readonly record struct Figure(PdfRectangle Bounds, string Transcription, int? PageNumber = null);

    /// <summary>
    /// A reconstructed visual line of the text layer. <see cref="MaxFontSize"/> (largest glyph point size) and
    /// <see cref="Bold"/> (line is predominantly bold) carry the typographic signal used for heading detection
    /// (#403). <see cref="StyledText"/> is the line's Markdown text with inline emphasis already applied
    /// (bold/italic runs wrapped and the rest inline-escaped) — used for rendering, while plain <see cref="Text"/>
    /// stays the detection signal (header/footer dedup, caption matching); it is <c>null</c> when the line was
    /// built without font data, in which case the renderer falls back to escaping <see cref="Text"/>. All
    /// font-derived fields default to neutral values so a line built by a test still behaves as before.
    /// </summary>
    public readonly record struct TextLine(
        PdfRectangle Bounds, string Text, double MaxFontSize = 0, bool Bold = false, string? StyledText = null);

    /// <summary>
    /// A reconstructed visual line plus the <see cref="Word"/> instances it was built from (left-to-right,
    /// matching <see cref="Text"/>). Lets <see cref="PdfRunningHeaderFooter"/> map a detected running
    /// header/footer line back to the exact words to drop from the page before rendering (#383). The word
    /// instances are the same references that were passed in, so callers can filter the original word list by
    /// reference identity.
    /// </summary>
    internal readonly record struct LineWithWords(PdfRectangle Bounds, string Text, IReadOnlyList<Word> Words);

    // Only bind a nearby text line to a figure when it reads like a figure/table caption. Keeps ordinary
    // adjacent body text from being relocated into the figure block.
    // Latin labels use a word boundary (so "figured"/"tablet" don't match). CJK labels (图/圖/図/表 — zh-Hans,
    // zh-Hant, ja) cannot use \b: an ideograph and a following digit are both word chars, so "图1" has no
    // boundary and the common space-less form would never match. Instead require the CJK label to be
    // followed (after optional space) by a figure number or a colon — matches "图1" / "表2" / "図1：" / "图 3"
    // while rejecting ordinary words like "图书馆" / "表面".
    private static readonly Regex CaptionPattern = new(
        @"^\s*((figure|fig\.?|table|exhibit|chart|diagram|plate)\b|[图圖図表]\s*[0-9０-９:：])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bound caption must be within this many median-line-heights of the figure (squared centroid distance).
    private const double CaptionMaxDistanceLineHeights = 6.0;

    // New paragraph when the baseline-to-baseline pitch between consecutive lines exceeds this many
    // median-line-heights (sits between single spacing ~1.0 and double spacing ~2.0).
    private const double ParagraphPitchLineHeights = 1.6;

    // Two blocks share a visual row (table-row grouping) when their vertical ranges overlap by at least this
    // ratio of the shorter block's height — see ClusterTableCandidateBlocks (#310 Phase B).
    private const double RowConnectVerticalOverlap = 0.5;

    // Consecutive visual rows belong to the same table candidate while the vertical gap between them stays
    // within this many median-block-heights. Wide enough for a table's inter-row pitch, tight enough to cut
    // the table off from a title / body paragraph above or below it (which sit farther away). See
    // ClusterTableCandidateBlocks (#310 Phase B).
    private const double RowGroupGapScale = 5.0;

    // Two consecutive visual rows only chain into one table candidate when the shorter row's height is at
    // least this fraction of the taller's. A tall multi-line paragraph / title block is then NOT chained onto
    // a table's short cell rows even when it sits close — and a paragraph-dominated page (whose page-wide
    // median height is large, inflating the gap threshold) can no longer over-merge (#329 review). See
    // ClusterTableCandidateBlocks.
    private const double RowHeightSimilarityRatio = 0.4;

    // Horizontal gutter scale for the structural break check (ClusterTableCandidateBlocks). Mirrors
    // PdfTableReconstruction.ColumnGutterScale so the cluster's notion of a column gutter matches the
    // reconstructor's: a horizontal gap wider than this many median-block-heights separates two columns.
    private const double ColumnGutterScale = 0.8;

    // A block at least this fraction of the page's content width is treated as a full-width "band separator"
    // for reading order (SegmentIntoReadingOrder): it spans every column, so content above and below it are
    // distinct horizontal bands. Sized to catch a full-width prose line (which reaches the right margin) while
    // leaving a shorter wrapped continuation / a table cell inside its band.
    private const double FullWidthReadingBandFraction = 0.7;

    /// <summary>
    /// Reconstructs visual lines from the page's words: clusters words whose vertical ranges overlap into
    /// one line, orders words left-to-right within a line, and returns lines top-to-bottom.
    /// </summary>
    public static IReadOnlyList<TextLine> GroupWordsIntoLines(IReadOnlyList<Word> words)
    {
        var withWords = GroupWordsIntoLinesWithWords(words);
        var lines = new List<TextLine>(withWords.Count);
        foreach (var line in withWords)
        {
            lines.Add(new TextLine(
                line.Bounds, line.Text, LineMaxFontSize(line.Words), IsLineBold(line.Words),
                StyledLineText(line.Words)));
        }

        return lines;
    }

    /// <summary>
    /// Builds the line's Markdown text with inline emphasis (#403): consecutive words of the same weight/slant
    /// form a run that is wrapped in <c>**…**</c> (bold), <c>_…_</c> (italic), or <c>***…***</c> (both); each
    /// run's content is inline-escaped exactly as the plain path would escape it, so only the deliberate
    /// emphasis markers stay live. Runs are space-joined, matching the plain line's word join.
    /// </summary>
    private static string StyledLineText(IReadOnlyList<Word> words)
    {
        var runs = new List<string>();
        var current = new List<string>();
        var currentBold = false;
        var currentItalic = false;

        void FlushRun()
        {
            if (current.Count == 0)
            {
                return;
            }

            var escaped = MarkdownText.EscapeInline(string.Join(" ", current));
            runs.Add((currentBold, currentItalic) switch
            {
                (true, true) => "***" + escaped + "***",
                (true, false) => "**" + escaped + "**",
                (false, true) => "_" + escaped + "_",
                _ => escaped
            });
            current.Clear();
        }

        foreach (var word in words)
        {
            var bold = IsWordBold(word);
            var italic = IsWordItalic(word);
            if (current.Count > 0 && (bold != currentBold || italic != currentItalic))
            {
                FlushRun();
            }

            currentBold = bold;
            currentItalic = italic;
            current.Add(word.Text);
        }

        FlushRun();
        return string.Join(" ", runs);
    }

    /// <summary>Whether the majority of a word's glyphs are bold.</summary>
    private static bool IsWordBold(Word word)
    {
        long total = 0, bold = 0;
        foreach (var letter in word.Letters)
        {
            total++;
            if (PdfHeadingScale.IsBoldFont(letter.FontName))
            {
                bold++;
            }
        }

        return total > 0 && bold * 2 >= total;
    }

    /// <summary>Whether the majority of a word's glyphs are italic.</summary>
    private static bool IsWordItalic(Word word)
    {
        long total = 0, italic = 0;
        foreach (var letter in word.Letters)
        {
            total++;
            if (PdfHeadingScale.IsItalicFont(letter.FontName))
            {
                italic++;
            }
        }

        return total > 0 && italic * 2 >= total;
    }

    /// <summary>The largest glyph point size on the line (its heading-candidate size, mirroring PyMuPDF4LLM's
    /// "largest font size of the line"), or 0 when no glyph carries a size.</summary>
    private static double LineMaxFontSize(IReadOnlyList<Word> words)
    {
        var max = 0.0;
        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                if (letter.PointSize > max)
                {
                    max = letter.PointSize;
                }
            }
        }

        return max;
    }

    /// <summary>Whether the majority of the line's glyphs are bold (a fully-bold heading / label line).</summary>
    private static bool IsLineBold(IReadOnlyList<Word> words)
    {
        long total = 0;
        long bold = 0;
        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                total++;
                if (PdfHeadingScale.IsBoldFont(letter.FontName))
                {
                    bold++;
                }
            }
        }

        return total > 0 && bold * 2 >= total;
    }

    /// <summary>
    /// Same line reconstruction as <see cref="GroupWordsIntoLines"/>, but each returned line also carries the
    /// <see cref="Word"/> instances it was built from (left-to-right, matching the line text). Used by
    /// <see cref="PdfRunningHeaderFooter"/> to drop the exact words of a detected running header/footer line
    /// from the page before rendering (#383).
    /// </summary>
    internal static IReadOnlyList<LineWithWords> GroupWordsIntoLinesWithWords(IReadOnlyList<Word> words)
    {
        var meaningful = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (meaningful.Count == 0)
        {
            return Array.Empty<LineWithWords>();
        }

        // Process top-to-bottom so a line cluster accretes its words in vertical order.
        var ordered = meaningful.OrderByDescending(w => w.BoundingBox.Top).ToList();

        var clusters = new List<List<Word>>();
        var clusterBounds = new List<PdfRectangle>();      // union of the line's words — the line bbox
        var clusterReference = new List<PdfRectangle>();   // last word added — the overlap reference

        foreach (var word in ordered)
        {
            var placed = false;
            for (var i = 0; i < clusters.Count; i++)
            {
                // Compare against the line's most-recently-added word, NOT the accreted union. A single
                // tall glyph (multi-line bracket / integral / tall CJK punctuation) would otherwise stretch
                // the union's vertical extent and, via the min-height overlap denominator, let a word from
                // the next physical line score >= 0.5 and merge into the wrong line.
                if (PdfGeometry.VerticalOverlapRatio(clusterReference[i], word.BoundingBox) >= 0.5)
                {
                    clusters[i].Add(word);
                    clusterBounds[i] = Union(clusterBounds[i], word.BoundingBox);
                    clusterReference[i] = word.BoundingBox;
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                clusters.Add(new List<Word> { word });
                clusterBounds.Add(word.BoundingBox);
                clusterReference.Add(word.BoundingBox);
            }
        }

        var lines = new List<LineWithWords>(clusters.Count);
        for (var i = 0; i < clusters.Count; i++)
        {
            var orderedWords = clusters[i].OrderBy(w => w.BoundingBox.Left).ToList();
            var text = string.Join(" ", orderedWords.Select(w => w.Text));
            lines.Add(new LineWithWords(clusterBounds[i], text, orderedWords));
        }

        return lines
            .OrderByDescending(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// Index of the text line nearest the image by RAGFlow-style squared centroid distance
    /// (<c>dis = dx² + dy²</c>), or <c>null</c> when there are no lines.
    /// </summary>
    public static int? FindNearestCaptionIndex(PdfRectangle imageBounds, IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var (ix, iy) = Centroid(imageBounds);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < lines.Count; i++)
        {
            var (lx, ly) = Centroid(lines[i].Bounds);
            var distance = Sq(lx - ix) + Sq(ly - iy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Renders the page to Markdown: text lines folded into gap-delimited paragraphs, figure
    /// transcriptions inlined at their reading position. A figure whose nearest line reads like a caption
    /// (and is close enough) consumes that line and renders it as the figure block's label, so the caption
    /// is not duplicated in the body text.
    /// </summary>
    public static string Render(IReadOnlyList<TextLine> lines, IReadOnlyList<Figure> figures, PdfHeadingScale? headingScale = null)
    {
        var medianLineHeight = lines.Count > 0
            ? PdfGeometry.Median(lines.Select(l => l.Bounds.Height))
            : 0.0;

        // 1. Bind captions (placement/labeling only — never sent to OCR). For each figure, bind the
        // NEAREST caption-like, not-yet-consumed line within range — looking past nearer non-caption or
        // already-bound lines so a genuine "Figure N:" caption is not left orphaned in the body text.
        var consumedLines = new HashSet<int>();
        var figureCaptions = new Dictionary<int, string>();
        if (lines.Count > 0)
        {
            var maxDistanceSq = Sq(CaptionMaxDistanceLineHeights * Math.Max(medianLineHeight, 1.0));
            for (var fi = 0; fi < figures.Count; fi++)
            {
                var (fx, fy) = Centroid(figures[fi].Bounds);
                var bestIndex = -1;
                var bestDistance = maxDistanceSq; // only consider lines within range
                for (var i = 0; i < lines.Count; i++)
                {
                    if (consumedLines.Contains(i) || !LooksLikeCaption(lines[i].Text))
                    {
                        continue;
                    }

                    var (lx, ly) = Centroid(lines[i].Bounds);
                    var distance = Sq(lx - fx) + Sq(ly - fy);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    consumedLines.Add(bestIndex);
                    figureCaptions[fi] = lines[bestIndex].Text;
                }
            }
        }

        // 2. Build the page-item list (unconsumed text lines + figures).
        var items = new List<Item>(lines.Count + figures.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!consumedLines.Contains(i))
            {
                // Inline-escape the digital-text-layer line so a "*"/"`"/"[" in the source cannot open an
                // emphasis / code / link span. The leading BLOCK marker is escaped later, once per folded
                // paragraph (FlushParagraph), since only a paragraph's first line sits at line start. A figure
                // transcription is OCR-provider Markdown (intentional structure) and is never escaped.
                // #403: classify the line as a heading from its font size + boldness (0 = body); a heading line
                // is emitted as its own "# …" block in step 4, never folded into a paragraph. Body lines carry
                // inline emphasis (StyledText, bold/italic runs already wrapped + escaped); a heading is rendered
                // plain (the "#" already signals it, so its text is not also bolded) via the inline-escaped Text.
                var headingLevel = headingScale?.ClassifyLine(lines[i].MaxFontSize, lines[i].Bold) ?? 0;
                var text = headingLevel == 0 && lines[i].StyledText is not null
                    ? lines[i].StyledText!
                    : MarkdownText.EscapeInline(lines[i].Text);
                items.Add(Item.ForText(lines[i].Bounds, text, headingLevel));
            }
        }

        for (var fi = 0; fi < figures.Count; fi++)
        {
            figureCaptions.TryGetValue(fi, out var caption);
            items.Add(Item.ForFigure(figures[fi].Bounds, figures[fi].Transcription, caption, figures[fi].PageNumber));
        }

        if (items.Count == 0)
        {
            return string.Empty;
        }

        // 3. Reading order: strictly top-to-bottom (Top descending), then left-to-right for items that
        // genuinely share a row (e.g. a figure beside a line). Items are already line-level (one per
        // visual line) plus figures, so a pure Top sort needs no banding — and banding would wrongly
        // tie a figure that sits just below an indented line into the line's row and reorder by Left.
        var orderedItems = items
            .OrderByDescending(it => it.Bounds.Top)
            .ThenBy(it => it.Bounds.Left)
            .ToList();

        // 4. Fold consecutive text lines into paragraphs; figures are standalone blocks. Split when the
        // baseline-to-baseline pitch (previous Top -> current Top, both descending) exceeds the threshold.
        var paragraphPitch = (medianLineHeight > 0 ? medianLineHeight : 0.0) * ParagraphPitchLineHeights;
        var blocks = new List<string>();
        var paragraph = new List<string>();
        double? previousTop = null;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                // Lines were inline-escaped at item creation; now neutralize a leading block marker on the
                // folded paragraph (e.g. a contract line "1. Definitions" / "- clause" / "# heading" that
                // would otherwise be re-parsed as a list / heading). The lines are space-joined into one
                // output line, so only its start is a line-start position.
                blocks.Add(MarkdownText.EscapeLineStarts(string.Join(" ", paragraph)));
                paragraph.Clear();
            }

            previousTop = null;
        }

        foreach (var item in orderedItems)
        {
            if (item.IsFigure)
            {
                FlushParagraph();
                // #371/#381: bracket the figure's OCR transcription with in-band *[Image OCR p:N]*…*[End OCR]*
                // provenance markers (MarkItDown-style). These stay in Document.Markdown (the egress payload): they
                // annotate the bracketed text as OCR for any consumer, and the pipeline reads them to recognize the
                // figure span (the classification embedded-document signal + the unified sub-document detection pass).
                // The caption is a digital-text-layer line bound to this figure, so escape it; item.Text is the OCR
                // transcription (already Markdown) and is emitted verbatim inside the markers.
                var figureMarkup = ImageOcrMarkup.Wrap(item.Text, item.PageNumber);
                blocks.Add(item.Caption is { Length: > 0 } caption
                    ? MarkdownText.EscapeBlockText(caption) + "\n\n" + figureMarkup
                    : figureMarkup);
                continue;
            }

            if (item.HeadingLevel > 0)
            {
                // #403: a heading line is its own block, never folded into a paragraph. Its text was already
                // inline-escaped; the leading "#"s are intentional Markdown (so it is NOT line-start-escaped).
                // Consecutive same-level heading blocks (a heading wrapped across lines / bands — e.g. the title
                // and its trailing 「書」) are stitched back together by MergeAdjacentHeadings at the page level.
                FlushParagraph();
                blocks.Add(new string('#', item.HeadingLevel) + " " + item.Text);
                continue;
            }

            if (previousTop is double top && paragraphPitch > 0 && (top - item.Bounds.Top) > paragraphPitch)
            {
                FlushParagraph();
            }

            paragraph.Add(item.Text);
            previousTop = item.Bounds.Top;
        }

        FlushParagraph();

        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// Stitches consecutive same-level heading blocks into one heading (#403): a heading that wrapped across
    /// visual lines / reading bands is rendered as adjacent <c>#…</c> blocks, and here they merge so e.g. the
    /// title's trailing 「書」 rejoins its title line. Operates by splitting on the blank-line block separator
    /// and re-joining it, so every non-heading block (paragraph, figure span) is returned unchanged.
    /// </summary>
    private static string MergeAdjacentHeadings(string pageMarkdown)
    {
        if (pageMarkdown.Length == 0)
        {
            return pageMarkdown;
        }

        var blocks = pageMarkdown.Split("\n\n");
        var merged = new List<string>(blocks.Length);
        foreach (var block in blocks)
        {
            var level = HeadingLevelOf(block);
            if (level > 0 && merged.Count > 0 && HeadingLevelOf(merged[^1]) == level)
            {
                // Append this heading line's text (after its duplicate "#…<space>" prefix) to the open heading.
                merged[^1] = merged[^1] + " " + block[(level + 1)..];
            }
            else
            {
                merged.Add(block);
            }
        }

        return string.Join("\n\n", merged);
    }

    /// <summary>The heading level (1..6) of a single-line <c>"#… text"</c> heading block, or 0 otherwise.</summary>
    private static int HeadingLevelOf(string block)
    {
        if (block.IndexOf('\n') >= 0)
        {
            return 0; // a heading block is always a single line
        }

        var hashes = 0;
        while (hashes < block.Length && block[hashes] == '#')
        {
            hashes++;
        }

        return hashes is >= 1 and <= 6 && hashes < block.Length && block[hashes] == ' ' ? hashes : 0;
    }

    /// <summary>
    /// Renders a page to Markdown with band-aware reading order (#310 Phase A): segments the page's
    /// words into layout blocks (<see cref="RecursiveXYCut"/>), orders the blocks band-aware
    /// (<see cref="SegmentIntoReadingOrder"/>: full-width bands, visual rows within a band), then renders each block in reading
    /// order through the single-region <see cref="Render(IReadOnlyList{TextLine}, IReadOnlyList{Figure})"/>
    /// path (line grouping + caption binding + paragraph folding) and joins the blocks. Embedded figures
    /// are merged into the block order by their placement bbox (nearest block by squared centroid
    /// distance), so figure inlining + caption association (#301) keep working on the column-aware order.
    /// <para>
    /// <b>Table regions (#310 Phase B).</b> Before the per-block paragraph pass, neighbouring blocks are
    /// re-aggregated into candidate table clusters; a cluster whose fragments confidently form a 2-D grid is
    /// emitted as a Markdown table (<see cref="PdfTableReconstruction"/>) at its lead block's reading
    /// position, and its blocks are skipped by the paragraph pass. Non-table blocks render exactly as in
    /// Phase A, so a page with no table is unchanged from #326. A figure bucketed into a table block is
    /// appended after the table (non-lossy). Pass <paramref name="reconstructTables"/> = <c>false</c> to
    /// disable this and keep the pure Phase A paragraph rendering.
    /// </para>
    /// <para>
    /// <b>Caption binding is per-block here.</b> Because each block is rendered through its own
    /// <see cref="Render(IReadOnlyList{TextLine}, IReadOnlyList{Figure})"/> pass, a figure binds a caption
    /// only from <i>its own</i> block. In the normal case a figure and its caption sit together in the
    /// same column/block, so this is unchanged from #301; the only degradation is the rare multi-column
    /// case where a figure's caption lands in a different block — the caption then stays as body text and
    /// the figure renders unlabeled. This is non-lossy (no text is dropped) and accepted for Phase A.
    /// </para>
    /// <para>
    /// <b>Non-lossy fallback.</b> When the page is a single region, segmentation faults, or a block layout
    /// would drop a word, this returns the flat single-region rendering instead — layout analysis is a
    /// best-effort quality improvement, never a correctness dependency, so no content is ever lost.
    /// </para>
    /// </summary>
    public static string RenderPage(
        IReadOnlyList<Word> words, IReadOnlyList<Figure> figures, bool reconstructTables = true,
        PdfHeadingScale? headingScale = null)
    {
        var markdown = RenderPageCore(words, figures, reconstructTables, headingScale);
        // #403: a heading that wrapped across lines / bands renders as adjacent same-level "# …" blocks; stitch
        // them back into one heading (e.g. the title and its trailing 「書」). A no-op when no headings exist.
        return headingScale is { HasHeadings: true } ? MergeAdjacentHeadings(markdown) : markdown;
    }

    private static string RenderPageCore(
        IReadOnlyList<Word> words, IReadOnlyList<Figure> figures, bool reconstructTables,
        PdfHeadingScale? headingScale)
    {
        IReadOnlyList<DlaTextBlock> orderedBlocks;
        try
        {
            orderedBlocks = SegmentIntoReadingOrder(words);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any segmenter/reading-order fault degrades to the flat path: the page still renders and no
            // content is lost (the digital text layer is the channel's primary, non-negotiable payload).
            // A cancellation is NOT swallowed — it propagates so a host/job shutdown aborts promptly.
            return Render(GroupWordsIntoLines(words), figures, headingScale);
        }

        // A single region (or no/one-block segmentation) is exactly the #301 behavior — render the whole
        // page as one column. This is also the path the existing single-column fixtures take. (A table is
        // almost always cut into several blocks, so single-block table reconstruction is not
        // attempted here — an accepted #329 first-pass limitation.)
        if (orderedBlocks.Count <= 1)
        {
            return Render(GroupWordsIntoLines(words), figures, headingScale);
        }

        // Non-lossy guard: RecursiveXYCut partitions every word into a leaf, so block words should account
        // for every meaningful input word. If a word went missing, discard the block layout and fall back
        // rather than emit a page that silently dropped contract text.
        var inputMeaningful = words.Count(w => !string.IsNullOrWhiteSpace(w.Text));
        var blockMeaningful = orderedBlocks.Sum(
            block => block.TextLines.Sum(line => line.Words.Count(w => !string.IsNullOrWhiteSpace(w.Text))));
        if (blockMeaningful < inputMeaningful)
        {
            return Render(GroupWordsIntoLines(words), figures, headingScale);
        }

        // Attach each figure to the block it reads with (nearest block centroid) so figure inlining +
        // caption binding run within that block's single-region stream.
        var figuresByBlock = AssignFiguresToBlocks(orderedBlocks, figures);

        // Phase B: re-aggregate neighbouring blocks into candidate table clusters and reconstruct the ones
        // that confidently form a grid. A non-table page yields no clusters, so the loop below is exactly the
        // Phase A per-block path; the host switch off -> empty maps -> identical to Phase A.
        var tables = reconstructTables ? DetectTableClusters(orderedBlocks) : TableClusters.Empty;

        var rendered = new List<string>(orderedBlocks.Count);
        var emittedTables = new HashSet<int>();
        for (var b = 0; b < orderedBlocks.Count; b++)
        {
            // A block that belongs to a reconstructed table: emit the table once, at the reading position of
            // the cluster's lead (first-encountered, lowest-index) block, then skip the cluster's blocks.
            if (tables.LeadByBlock.TryGetValue(b, out var lead))
            {
                if (emittedTables.Add(lead))
                {
                    rendered.Add(tables.MarkdownByLead[lead]);

                    // A table region is text; a figure that AssignFiguresToBlocks happened to bucket into one
                    // of its blocks is rare, but must not be lost — append it after the table (non-lossy).
                    var clusterFigures = tables.BlocksByLead[lead]
                        .SelectMany(bi => figuresByBlock[bi])
                        .ToList();
                    if (clusterFigures.Count > 0)
                    {
                        var figureMarkdown = Render(Array.Empty<TextLine>(), clusterFigures);
                        if (!string.IsNullOrWhiteSpace(figureMarkdown))
                        {
                            rendered.Add(figureMarkdown);
                        }
                    }
                }

                continue;
            }

            // Re-group the block's words into visual lines with the existing robust clustering (rather than
            // the segmenter's own lines), so tall-glyph / CJK handling and paragraph folding are unchanged.
            var blockWords = orderedBlocks[b].TextLines.SelectMany(line => line.Words).ToList();
            var blockLines = GroupWordsIntoLines(blockWords);
            var blockFigures = figuresByBlock[b];
            if (blockLines.Count == 0 && blockFigures.Count == 0)
            {
                continue;
            }

            var blockMarkdown = Render(blockLines, blockFigures, headingScale);
            if (!string.IsNullOrWhiteSpace(blockMarkdown))
            {
                rendered.Add(blockMarkdown);
            }
        }

        return string.Join("\n\n", rendered);
    }

    /// <summary>
    /// Table clusters found on a page: which reconstructed table (if any) each block belongs to, the rendered
    /// Markdown per lead block, and the blocks making up each cluster (for figure salvage).
    /// </summary>
    private sealed class TableClusters
    {
        public static readonly TableClusters Empty = new();

        /// <summary>Lead (lowest-index) block of a cluster -> its rendered Markdown table.</summary>
        public Dictionary<int, string> MarkdownByLead { get; } = new();

        /// <summary>Any block that is part of a reconstructed table -> that cluster's lead block index.</summary>
        public Dictionary<int, int> LeadByBlock { get; } = new();

        /// <summary>Lead block index -> all block indices in the cluster (ascending).</summary>
        public Dictionary<int, List<int>> BlocksByLead { get; } = new();
    }

    /// <summary>
    /// Re-aggregates neighbouring blocks into clusters (<see cref="ClusterTableCandidateBlocks"/>) and
    /// reconstructs the clusters that confidently form a Markdown table from their <b>words</b>
    /// (<see cref="PdfTableReconstruction"/>). RecursiveXYCut splits a table into several blocks — per-cell at
    /// a generous row pitch, per-column at a tight one — so the cluster's words (not its blocks) are fed to
    /// the reconstructor, which derives the grid from glyph geometry either way. A cluster that does not
    /// reconstruct as a table is absent from the result, so its blocks fall through to the Phase A paragraph path.
    /// </summary>
    private static TableClusters DetectTableClusters(IReadOnlyList<DlaTextBlock> blocks)
    {
        var medianHeight = PdfGeometry.Median(blocks.Select(b => b.BoundingBox.Height));
        if (medianHeight <= 0)
        {
            return TableClusters.Empty;
        }

        var gutterThreshold = medianHeight * ColumnGutterScale;
        var result = new TableClusters();
        foreach (var clusterRows in ClusterTableCandidateBlocks(blocks, medianHeight))
        {
            // Peel leading heading / caption / intro rows (the "第3条（料金）" heading + "本業務の料金は…" intro
            // above the grid, the "別紙1 料金表" caption) before reconstruction. They enter the cluster from the
            // top — where no column structure is established yet, so the chaining bridge-guard cannot reject
            // them — and then either become spurious leading table rows or, worse, a full-width intro line
            // bridges and MERGES two real columns (the 料金表's 区分+内容). Trailing body text needs no peel: by
            // the time the grid is below it, the column structure exists and the chaining bridge-guard already
            // refuses to chain it in.
            var coreRows = TrimLeadingNonGridRows(clusterRows, blocks, gutterThreshold);
            if (coreRows.Count == 0)
            {
                continue;
            }

            var coreBlocks = coreRows.SelectMany(row => row).ToList();

            // Reconstruct from the cluster's WORDS, not its blocks: RecursiveXYCut's block granularity is not
            // stable for tables (#329 review) — a generous row pitch is cut into per-cell blocks, but a tight
            // row pitch is cut into per-COLUMN blocks (each column one tall block of stacked rows). Feeding
            // words lets TryRender derive the grid from real glyph geometry either way; its column x-projection
            // + visual-row clustering + wrapped-cell merge handle both, including a 備考 cell wrapping to two
            // lines (whether or not the segmenter pre-merged it).
            var cells = coreBlocks
                .SelectMany(bi => blocks[bi].TextLines.SelectMany(line => line.Words))
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => new PdfTableReconstruction.Cell(word.BoundingBox, word.Text))
                .ToList();

            var table = PdfTableReconstruction.TryRender(cells);
            if (table is null)
            {
                continue;
            }

            // Lead = lowest-index core block: the table is emitted at its reading position, and any peeled
            // (lower-index) heading/intro block falls through to the paragraph pass at its own earlier position,
            // so the heading still renders above the table.
            var lead = coreBlocks.Min();
            result.MarkdownByLead[lead] = table;
            result.BlocksByLead[lead] = coreBlocks;
            foreach (var bi in coreBlocks)
            {
                result.LeadByBlock[bi] = lead;
            }
        }

        return result;
    }

    /// <summary>
    /// Drops leading rows of a table-candidate cluster that are not grid rows — a section heading, a table
    /// caption, or an introductory sentence sitting just above the grid — returning the remaining rows
    /// (top to bottom). A leading row is peeled when it is <b>mono-column</b> (all its blocks fall in a single
    /// column band: a heading / caption / title) or <b>gutter-bridging</b> (a block spans across a column
    /// gutter: a full-width intro sentence). The column model is recomputed after each peel, so removing a
    /// full-width intro that had merged two columns lets those columns re-separate for the rows below it.
    /// Peeling stops at the first real grid row (the header or a data row, which spans ≥2 columns without
    /// bridging). Only the leading edge is peeled: a mono-column row in the interior or at the bottom may be a
    /// sparse data row or a wrapped-cell continuation and is kept. Peeled blocks are simply excluded from the
    /// table, so they fall through to the paragraph path at their own reading position (non-lossy).
    /// </summary>
    private static List<List<int>> TrimLeadingNonGridRows(
        List<List<int>> clusterRows, IReadOnlyList<DlaTextBlock> blocks, double gutterThreshold)
    {
        var rows = new List<List<int>>(clusterRows);
        while (rows.Count > 0)
        {
            // Seed the column model from the remaining MULTI-CELL rows only, so a leading full-width title or a
            // centred stamp/watermark (both single-block rows) neither collapses the columns to one band nor
            // injects a spurious one — the grid below stays visible and the title/watermark is then peeled.
            var bands = ColumnBandsFromRows(rows, blocks, gutterThreshold);
            if (bands.Count < 2)
            {
                // No column structure across the remaining rows → not a grid; let TryRender reject it as a whole
                // rather than peel every row one by one.
                break;
            }

            if (IsLeadingNonGridRow(rows[0], bands, blocks))
            {
                rows.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether <paramref name="row"/> reads as a heading / caption / intro rather than a grid row, given the
    /// current column <paramref name="bands"/>: true when every block sits in one band (mono-column) or any
    /// block bridges a gutter (a full-width line). A genuine header / data row spans ≥2 bands without bridging.
    /// </summary>
    private static bool IsLeadingNonGridRow(
        IReadOnlyList<int> row, IReadOnlyList<(double Left, double Right)> bands, IReadOnlyList<DlaTextBlock> blocks)
    {
        var occupied = new HashSet<int>();
        foreach (var bi in row)
        {
            var box = blocks[bi].BoundingBox;
            if (BridgesAnyGutter(box, bands))
            {
                return true;
            }

            occupied.Add(BandOf(box, bands));
        }

        return occupied.Count <= 1;
    }

    /// <summary>Index of the column band containing the box's horizontal centre (nearest band as a fallback).</summary>
    private static int BandOf(PdfRectangle box, IReadOnlyList<(double Left, double Right)> bands)
    {
        var centre = (box.Left + box.Right) / 2.0;
        for (var i = 0; i < bands.Count; i++)
        {
            if (centre >= bands[i].Left && centre <= bands[i].Right)
            {
                return i;
            }
        }

        var nearest = 0;
        var best = double.MaxValue;
        for (var i = 0; i < bands.Count; i++)
        {
            var d = Math.Min(Math.Abs(centre - bands[i].Left), Math.Abs(centre - bands[i].Right));
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Groups blocks into table-candidate clusters, each returned as its list of <i>visual rows</i> (top to
    /// bottom, every row a list of block indices). RecursiveXYCut cuts a table into per-CELL blocks (its column
    /// gutters and row gaps both exceed the cut threshold), so a cluster is built in two steps: (1) union blocks
    /// whose vertical ranges overlap into visual rows; (2) chain vertically adjacent rows (gap within
    /// <see cref="RowGroupGapScale"/> median-heights, comparable height, no column-bridging) into one cluster —
    /// which stops at the larger gap to a title / body paragraph above or below, and at a full-width body line
    /// below the grid. The row structure is preserved (not flattened) so <see cref="DetectTableClusters"/> can
    /// peel leading heading / caption / intro rows before reconstruction. A lone row forms a singleton cluster
    /// (later rejected by <see cref="PdfTableReconstruction.TryRender"/> for having too few rows).
    /// </summary>
    private static List<List<List<int>>> ClusterTableCandidateBlocks(IReadOnlyList<DlaTextBlock> blocks, double medianHeight)
    {
        var n = blocks.Count;

        // 1. Visual rows: union blocks whose vertical ranges overlap.
        var parent = new int[n];
        for (var i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (PdfGeometry.VerticalOverlapRatio(blocks[i].BoundingBox, blocks[j].BoundingBox) >= RowConnectVerticalOverlap)
                {
                    parent[Find(i)] = Find(j);
                }
            }
        }

        var rowGroups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!rowGroups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                rowGroups[root] = list;
            }

            list.Add(i);
        }

        // Order the rows top to bottom by their highest edge.
        var rows = rowGroups.Values
            .OrderByDescending(group => group.Max(bi => blocks[bi].BoundingBox.Top))
            .ToList();

        // 2. Chain rows whose vertical gap stays within the threshold AND whose height is comparable AND whose
        // blocks do not break the table's column structure, into one cluster. Three guards:
        //   - gap: a wider vertical gap cuts the table off from a title / body paragraph farther above or below.
        //   - height: a tall multi-line body / title block (a RecursiveXYCut paragraph leaf) is not chained onto
        //     a table's short cell rows even when it sits close, and a paragraph-dominated page (large page-wide
        //     median -> large gap threshold) cannot over-merge (#329 review).
        //   - column structure (#310 follow-up): a row containing a block that BRIDGES the candidate's already
        //     established column gutters (a full-page-width note / clause paragraph below the grid, e.g. the
        //     日本語 料金表's "※上記金額…" / "本契約の成立を…" footnotes) is body text, not a table row — without
        //     this guard those single-line full-width paragraphs are gap+height-comparable to the cell rows, so
        //     they get chained in, collapse the column bands when the whole cluster is reconstructed, and sink
        //     the entire table back to paragraph linearization. The gap/height guards alone cannot separate them
        //     (the footnote sits one ordinary line-gap below the last cell row); only the column geometry can.
        //     The column model is seeded only from the cluster's MULTI-CELL rows (see ColumnBandsFromRows), so a
        //     full-width title or a centred stamp/watermark above the grid contributes no column and cannot
        //     disable this guard.
        // Any guard failing starts a new cluster, so the trailing body text reconstructs (or degrades) on its own.
        var gapThreshold = medianHeight * RowGroupGapScale;
        var gutterThreshold = medianHeight * ColumnGutterScale;
        var clusters = new List<List<List<int>>>();
        var current = new List<List<int>> { rows[0] };
        var currentBottom = rows[0].Min(bi => blocks[bi].BoundingBox.Bottom);
        var currentRowHeight = PdfGeometry.Median(rows[0].Select(bi => blocks[bi].BoundingBox.Height));
        for (var i = 1; i < rows.Count; i++)
        {
            var rowTop = rows[i].Max(bi => blocks[bi].BoundingBox.Top);
            var rowHeight = PdfGeometry.Median(rows[i].Select(bi => blocks[bi].BoundingBox.Height));
            var tallerHeight = Math.Max(currentRowHeight, rowHeight);
            var heightComparable = tallerHeight <= 0
                || Math.Min(currentRowHeight, rowHeight) >= tallerHeight * RowHeightSimilarityRatio;

            if (currentBottom - rowTop <= gapThreshold
                && heightComparable
                && !RowBridgesColumns(current, rows[i], blocks, gutterThreshold))
            {
                current.Add(rows[i]);
                currentBottom = Math.Min(currentBottom, rows[i].Min(bi => blocks[bi].BoundingBox.Bottom));
                currentRowHeight = rowHeight;
            }
            else
            {
                clusters.Add(current);
                current = new List<List<int>> { rows[i] };
                currentBottom = rows[i].Min(bi => blocks[bi].BoundingBox.Bottom);
                currentRowHeight = rowHeight;
            }
        }

        clusters.Add(current);
        return clusters;
    }

    /// <summary>
    /// Whether any block of <paramref name="candidateRow"/> bridges a column gutter already established by the
    /// rows accumulated in <paramref name="clusterRows"/> — i.e. its horizontal extent spans across the empty
    /// space that separates two of the candidate table's columns. Such a block is a full-width body line (a
    /// footnote / clause paragraph / section heading), not a table cell, so the row must not be chained into the
    /// table candidate. Returns <c>false</c> until at least two column bands exist (no gutter to bridge yet), so
    /// the leading rows of a table still accumulate normally.
    /// </summary>
    private static bool RowBridgesColumns(
        IReadOnlyList<IReadOnlyList<int>> clusterRows,
        IReadOnlyList<int> candidateRow,
        IReadOnlyList<DlaTextBlock> blocks,
        double gutterThreshold)
    {
        var bands = ColumnBandsFromRows(clusterRows, blocks, gutterThreshold);
        if (bands.Count < 2)
        {
            return false;
        }

        foreach (var bi in candidateRow)
        {
            if (BridgesAnyGutter(blocks[bi].BoundingBox, bands))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="box"/> straddles the empty gutter between any two adjacent column
    /// <paramref name="bands"/> — i.e. it reaches into band k (or its left gutter) AND past the start of band
    /// k+1. A real table cell never does (the gutter is empty by definition); a full-width body line (heading /
    /// footnote / clause paragraph) does.
    /// </summary>
    private static bool BridgesAnyGutter(PdfRectangle box, IReadOnlyList<(double Left, double Right)> bands)
    {
        var left = Math.Min(box.Left, box.Right);
        var right = Math.Max(box.Left, box.Right);
        for (var k = 0; k < bands.Count - 1; k++)
        {
            if (left <= bands[k].Right && right >= bands[k + 1].Left)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Projects the boxes' horizontal extents onto the x-axis and splits them into column bands at gutters wider
    /// than <paramref name="gutterThreshold"/> (overlapping/abutting extents merge into one band). Mirrors
    /// <see cref="PdfTableReconstruction"/>'s own column detection so the cluster's column model matches the one
    /// the reconstructor will later apply. Each band is the <c>[left, right]</c> extent of one column's content.
    /// </summary>
    private static List<(double Left, double Right)> ColumnBands(IEnumerable<PdfRectangle> boxes, double gutterThreshold)
    {
        var intervals = boxes
            .Select(b => (Left: Math.Min(b.Left, b.Right), Right: Math.Max(b.Left, b.Right)))
            .OrderBy(iv => iv.Left)
            .ToList();

        var bands = new List<(double Left, double Right)>();
        if (intervals.Count == 0)
        {
            return bands;
        }

        var left = intervals[0].Left;
        var right = intervals[0].Right;
        for (var i = 1; i < intervals.Count; i++)
        {
            var iv = intervals[i];
            if (iv.Left - right > gutterThreshold)
            {
                bands.Add((left, right));
                left = iv.Left;
                right = iv.Right;
            }
            else
            {
                right = Math.Max(right, iv.Right);
            }
        }

        bands.Add((left, right));
        return bands;
    }

    /// <summary>
    /// Column bands for a table candidate, seeded <b>only from its multi-cell rows</b> (rows of ≥2 blocks). A
    /// single-block row reveals no column structure — it is a title, a section heading, a table caption, a
    /// stamp / watermark, or a full-width prose line, and its one box spans no gutter — so letting it seed the
    /// model would either collapse the columns to one band (a full-width title masking the grid below it) or
    /// inject a spurious column (a centred watermark like the 契約書案 draft stamp above the 委託者/受託者
    /// key-value grid). Only a row that actually carries several cells shows where the columns and their gutters
    /// are. The candidate row being tested for bridging is judged against this clean model, so a full-width body
    /// line still bridges and is rejected, while the title/watermark simply contribute nothing.
    /// </summary>
    private static List<(double Left, double Right)> ColumnBandsFromRows(
        IReadOnlyList<IReadOnlyList<int>> rows, IReadOnlyList<DlaTextBlock> blocks, double gutterThreshold)
    {
        var boxes = rows
            .Where(row => row.Count >= 2)
            .SelectMany(row => row)
            .Select(bi => blocks[bi].BoundingBox);
        return ColumnBands(boxes, gutterThreshold);
    }

    /// <summary>
    /// Segments a page's words into layout blocks and returns them in reading order. Uses
    /// <see cref="RecursiveXYCut"/> — a top-down "Manhattan layout" segmenter that cuts the page along
    /// whitespace valleys wider than the dominant font metrics, separating columns (vertical gutter cut)
    /// and paragraphs (horizontal line-gap cut). This fits the column/table-structured target corpus
    /// (contracts / invoices) better than the bottom-up Docstrum clustering.
    /// <para>
    /// <b>Band-aware reading order.</b> The blocks are first split into horizontal bands at full-width blocks
    /// (a full-width prose line / footnote spans every column, so content above and below it reads separately),
    /// and only <i>within</i> each band are blocks ordered by visual row (<see cref="OrderBandRowWise"/>:
    /// top-to-bottom rows, left-to-right within a row). A plain page-wide column-wise pass reads each column
    /// top-to-bottom across the whole page, which on a single-column page interleaved with a table reads the
    /// table's columns vertically and captures the surrounding full-width clause prose into them — scrambling
    /// the clause text. Banding plus row-wise ordering reads single-column prose between tables in true
    /// top-to-bottom order while keeping a genuine side-by-side row (a label beside its body, the #310 case)
    /// reading left-to-right. Returns an empty list when there are no meaningful words.
    /// </para>
    /// </summary>
    private static IReadOnlyList<DlaTextBlock> SegmentIntoReadingOrder(IReadOnlyList<Word> words)
    {
        // The segmenter needs glyph geometry; whitespace-only tokens carry none and only add noise.
        var meaningful = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (meaningful.Count == 0)
        {
            return Array.Empty<DlaTextBlock>();
        }

        var blocks = new RecursiveXYCut().GetBlocks(meaningful);
        if (blocks.Count <= 1)
        {
            return blocks;
        }

        return OrderByBandsThenColumns(blocks);
    }

    /// <summary>
    /// Orders blocks into reading order by splitting them into horizontal bands at full-width separators and
    /// ordering each band by <see cref="OrderBandRowWise">visual row</see>. A block whose width is at least
    /// <see cref="FullWidthReadingBandFraction"/> of the page's content width spans every column and forms its
    /// own band; each run of narrower blocks between separators forms a band; bands are read top-to-bottom.
    /// Within a multi-block band, blocks are grouped into visual rows read top-to-bottom (then left-to-right
    /// within a row), so a stamp / wrapped title char that sits above a table but in a column to its side reads
    /// before the table, not after it; the table cells of a band reconstruct independently of this order.
    /// </summary>
    private static IReadOnlyList<DlaTextBlock> OrderByBandsThenColumns(IReadOnlyList<DlaTextBlock> blocks)
    {
        var minLeft = blocks.Min(b => Math.Min(b.BoundingBox.Left, b.BoundingBox.Right));
        var maxRight = blocks.Max(b => Math.Max(b.BoundingBox.Left, b.BoundingBox.Right));
        var fullWidth = (maxRight - minLeft) * FullWidthReadingBandFraction;

        // Walk top-to-bottom, flushing the accumulated narrow-block band whenever a full-width separator is hit
        // (the separator is then its own band). This yields the bands already in top-to-bottom order.
        var bands = new List<List<DlaTextBlock>>();
        var current = new List<DlaTextBlock>();
        foreach (var block in blocks.OrderByDescending(b => b.BoundingBox.Top))
        {
            if (Math.Abs(block.BoundingBox.Width) >= fullWidth)
            {
                if (current.Count > 0)
                {
                    bands.Add(current);
                    current = new List<DlaTextBlock>();
                }

                bands.Add(new List<DlaTextBlock> { block });
            }
            else
            {
                current.Add(block);
            }
        }

        if (current.Count > 0)
        {
            bands.Add(current);
        }

        var result = new List<DlaTextBlock>(blocks.Count);
        foreach (var band in bands)
        {
            if (band.Count == 1)
            {
                result.Add(band[0]);
            }
            else
            {
                result.AddRange(OrderBandRowWise(band));
            }
        }

        return result;
    }

    /// <summary>
    /// Orders the blocks of a single band in reading order: group them into visual rows by vertical overlap,
    /// read the rows top-to-bottom, and read each row left-to-right. A row-wise order (rather than a page-wide
    /// column-wise one) is correct here because a band has already been cut at its full-width separators, so its
    /// remaining content reads top-to-bottom; ordering by column instead would place a block that sits in its
    /// own x-column but higher up (e.g. the page-1 「契約書案」 draft stamp / the title's wrapped 「書」, which sit
    /// above the key-value table but in a column to its right) AFTER the table rather than before it. A genuine
    /// side-by-side row (a label beside its body, the #310 case) is a single visual row and still reads
    /// left-to-right; a table's cells reconstruct independently of this order.
    /// </summary>
    private static IReadOnlyList<DlaTextBlock> OrderBandRowWise(IReadOnlyList<DlaTextBlock> band)
    {
        // Cluster blocks into visual rows: a block joins a row when it vertically overlaps that row's most
        // recently added block (compared against the last block, not an accreted union, so a tall block cannot
        // stretch a row and pull in a block from the next one — mirrors GroupWordsIntoLines).
        var rows = new List<List<DlaTextBlock>>();
        foreach (var block in band.OrderByDescending(b => b.BoundingBox.Top))
        {
            var placed = false;
            foreach (var row in rows)
            {
                if (PdfGeometry.VerticalOverlapRatio(row[^1].BoundingBox, block.BoundingBox) >= RowConnectVerticalOverlap)
                {
                    row.Add(block);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                rows.Add(new List<DlaTextBlock> { block });
            }
        }

        var result = new List<DlaTextBlock>(band.Count);
        foreach (var row in rows.OrderByDescending(r => r.Max(b => b.BoundingBox.Top)))
        {
            result.AddRange(row.OrderBy(b => b.BoundingBox.Left));
        }

        return result;
    }

    /// <summary>
    /// Buckets figures by the block they read with: each figure is assigned to the block whose bounding
    /// box is nearest by squared centroid distance. Every figure lands in exactly one bucket (no figure is
    /// dropped); <paramref name="blocks"/> is expected to be non-empty.
    /// </summary>
    private static IReadOnlyList<List<Figure>> AssignFiguresToBlocks(
        IReadOnlyList<DlaTextBlock> blocks, IReadOnlyList<Figure> figures)
    {
        var byBlock = new List<List<Figure>>(blocks.Count);
        for (var b = 0; b < blocks.Count; b++)
        {
            byBlock.Add(new List<Figure>());
        }

        foreach (var figure in figures)
        {
            var (fx, fy) = Centroid(figure.Bounds);
            var best = 0;
            var bestDistance = double.MaxValue;
            for (var b = 0; b < blocks.Count; b++)
            {
                var (bx, by) = Centroid(blocks[b].BoundingBox);
                var distance = Sq(bx - fx) + Sq(by - fy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = b;
                }
            }

            byBlock[best].Add(figure);
        }

        return byBlock;
    }

    private static bool LooksLikeCaption(string text) => CaptionPattern.IsMatch(text);

    private static PdfRectangle Union(PdfRectangle a, PdfRectangle b)
        => new(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Bottom, b.Bottom),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Top, b.Top));

    private static (double X, double Y) Centroid(PdfRectangle r)
        => ((r.Left + r.Right) / 2.0, (r.Bottom + r.Top) / 2.0);

    private static double Sq(double v) => v * v;

    private readonly record struct Item(PdfRectangle Bounds, string Text, bool IsFigure, string? Caption, int? PageNumber, int HeadingLevel)
    {
        public static Item ForText(PdfRectangle bounds, string text, int headingLevel = 0)
            => new(bounds, text, false, null, null, headingLevel);

        public static Item ForFigure(PdfRectangle bounds, string text, string? caption, int? pageNumber)
            => new(bounds, text, true, caption, pageNumber, 0);
    }
}

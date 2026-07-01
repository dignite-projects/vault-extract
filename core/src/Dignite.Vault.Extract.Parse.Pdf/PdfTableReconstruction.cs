using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Core;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Reconstructs a candidate table region of a PDF's <b>digital text layer</b> into a GFM Markdown table
/// (#310 Phase B). The input is a flat set of positioned text fragments (<see cref="Cell"/>, one per
/// PdfPig <c>Word</c>); the output is a Markdown table, or <c>null</c> when the region does not confidently
/// read as a 2-D grid.
/// <para>
/// <b>Why this exists.</b> Phase A (#326) orders blocks column-wise but still linearizes each block as
/// paragraphs, so a table row's cells are emitted as separate fragments and a wrapped cell (the #310 日本語
/// 料金表 <c>備考</c> cell, which spills onto a second visual line) interleaves with its row siblings —
/// <c>…付随する無</c> … (other cells) … <c>償提供。税別対象外</c>. Grouping a row's cells into one Markdown
/// table row keeps a wrapped cell intact in its own cell.
/// </para>
/// <para>
/// <b>Non-lossy by construction.</b> Every input fragment is assigned to exactly one <c>(row, column)</c>
/// cell, so a successful render contains all the region's text. When the region fails any grid test
/// (too few columns/rows, rows not aligned across columns, too sparse) this returns <c>null</c> and the
/// caller (<see cref="PdfReadingOrder.RenderPage"/>) keeps the Phase A paragraph linearization — never
/// forcing a bad table, never dropping a fragment.
/// </para>
/// <para>
/// <b>First solid pass — deferred (per #329 out-of-scope).</b> Borderless tables are handled (detection is
/// whitespace-geometry only, never relies on ruling lines), but row-spanning / column-spanning / nested
/// tables are not modeled. Two ambiguities plain geometry cannot resolve are settled on the safe side:
/// (a) a line filling the SAME columns as the row above is always a new row, never a multi-cell wrap, so the
/// rare "several cells of one row each wrap to an aligned second line" renders as an extra row rather than
/// risk merging two genuine rows; (b) a table whose row gap is as tight as single-line leading may fold a
/// sparse next row into a wrapped-cell continuation. All such cases stay non-lossy (text is preserved, only
/// the grouping may be imperfect) and degrade rather than corrupt.
/// </para>
/// <para>
/// PDF user space is bottom-left origin (larger Y = higher on the page), matching <see cref="PdfReadingOrder"/>.
/// </para>
/// </summary>
internal static class PdfTableReconstruction
{
    /// <summary>A positioned text fragment (one PdfPig <c>Word</c>) — the lightweight, geometry-testable input.</summary>
    public readonly record struct Cell(PdfRectangle Bounds, string Text);

    // A table needs at least this many columns and rows; a 1xN or Nx1 strip is not a table.
    private const int MinColumns = 2;
    private const int MinRows = 2;

    // Two fragments belong to the same column unless a horizontal gap wider than this (in median-glyph-height
    // units) separates them — the column gutter. Sized so an ordinary inter-word space (a fraction of the
    // glyph height) never reads as a column break, while a real gutter does. Also the minimum WIDTH of a
    // column-separating whitespace street in DetectColumnBands.
    private const double ColumnGutterScale = 0.8;

    // A column gutter may be BRIDGED (filled) by a wide cell in a minority of rows and still be a real gutter;
    // DetectColumnBands keeps the boundary while the street stays empty in all but this fraction of the rows
    // (#446). Sized so up to ~a third of rows can bridge a gutter (a stray wide value / a numeric column whose
    // longest entry reaches toward its neighbour) before the boundary dissolves — yet a column genuinely FILLED
    // in half its rows still reads as content, never a gutter. A table too sparse for that distinction to hold
    // is rejected downstream anyway (MinFillRatio), so erring toward keeping the boundary is safe.
    private const double GutterBridgeToleranceFraction = 0.34;

    // Two fragments share a visual line when their vertical ranges overlap by at least this ratio (of the
    // shorter one). Mirrors PdfReadingOrder.GroupWordsIntoLines so line grouping is consistent.
    private const double RowOverlapRatio = 0.5;

    // A visual line is a wrapped-cell CONTINUATION of the row above (rather than a new row) only when it
    // occupies a strict subset of that row's columns AND the vertical GAP to it (bottom-to-top, NOT
    // baseline-to-baseline) is within this many median-glyph-heights. Sized below a typical inter-row table
    // gap (which carries cell padding) so a separate sparse row is not swallowed, while a single-leading wrap
    // is folded. A wrap's gap and a tight row's gap are not perfectly separable from geometry alone (#329
    // review tightened this from 1.35; the strict-subset requirement is the primary guard) — see class remarks.
    private const double ContinuationPitchScale = 0.9;

    // At least this fraction of the MERGED table rows must span >= 2 columns (computed after wrapped-cell
    // continuation lines are folded in — #329 review). A multi-column BODY layout (Phase A's target) has each
    // row living in a single column (left/right columns' lines don't share a y-band), so it fails this test
    // and correctly degrades to paragraphs; a real table's rows span the columns.
    private const double MinCrossColumnRowRatio = 0.5;

    // The grid must be at least this filled (non-empty cells / rows x columns). Rejects a sparse scatter that
    // happens to land in a few column bands but isn't a real table.
    private const double MinFillRatio = 0.5;

    /// <summary>
    /// Renders <paramref name="cells"/> as a GFM Markdown table, or returns <c>null</c> when they do not form
    /// a confident grid (the caller then keeps the Phase A paragraph rendering).
    /// </summary>
    public static string? TryRender(IReadOnlyList<Cell> cells)
    {
        var meaningful = cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();
        if (meaningful.Count == 0)
        {
            return null;
        }

        // The whole geometry is scaled by the median fragment height (the dominant glyph size), so the
        // thresholds are font-size independent.
        var medianHeight = PdfGeometry.Median(meaningful.Select(c => c.Bounds.Height));
        if (medianHeight <= 0)
        {
            return null;
        }

        // 1. Visual lines: cluster fragments whose vertical ranges overlap (same as the paragraph path), top
        // to bottom. Built first because column detection is now row-aware (#446).
        var visualLines = GroupIntoVisualLines(meaningful);
        if (visualLines.Count < MinRows)
        {
            return null;
        }

        // 2. Columns: split the fragments at the vertical whitespace streets between columns. A street must be
        // empty across (almost) every row, so a wide cell reaching into a gutter in a minority of rows no longer
        // merges two columns for the whole table (#446). Robust to left/right/centre cell alignment — a column's
        // content lands in its band whatever its alignment, and the gutter between bands is empty.
        var columns = DetectColumnBands(visualLines, medianHeight * ColumnGutterScale);
        if (columns.Count < MinColumns)
        {
            return null;
        }

        // 3. Map each visual line to the columns it occupies.
        var lineColumns = new List<Dictionary<int, List<Cell>>>(visualLines.Count);
        foreach (var line in visualLines)
        {
            var byColumn = new Dictionary<int, List<Cell>>();
            foreach (var cell in line)
            {
                var column = ColumnOf(cell, columns);
                if (!byColumn.TryGetValue(column, out var list))
                {
                    list = new List<Cell>();
                    byColumn[column] = list;
                }

                list.Add(cell);
            }

            lineColumns.Add(byColumn);
        }

        // 4. Fold wrapped-cell continuation lines into their parent row.
        var rows = MergeRows(visualLines, lineColumns, columns.Count, medianHeight * ContinuationPitchScale);

        // 4b. Re-attach stray single-cell fragments: a cell taller than its row's single-line siblings wraps to a
        // second visual line that falls OUTSIDE the row's y-band (above or below it), so step 4 — which only
        // folds a continuation tight against the CURRENT row — leaves it as its own mono-column row interleaved
        // with the data row (the #310 料金表 備考 cell "本契約に付随する無償提供。税別対象外", whose two lines
        // bracket the "0円" row). Pull such a fragment into the adjacent data row's empty cell.
        CoalesceStrayCellFragments(rows, medianHeight * ContinuationPitchScale);

        // 4c. Merge a run of consecutive single-column lines that are tight against each other (within the
        // continuation pitch) into one row. This catches a key-value row whose short label and multi-line value
        // sit on SEPARATE visual lines in DIFFERENT columns (the 契約目的 row: label "契約目的" alone on one line,
        // its value "甲の…定める。" wrapping across the line above and the line below it) — none of those lines is
        // a multi-cell "host" for step 4b, so they would otherwise render as three half-empty rows that sever the
        // label from its value. A run of single-column lines this tightly stacked is one record's cells, not
        // several records (which sit a full row-pitch apart); a multi-cell record line breaks the run, so two
        // real records are never merged.
        CoalesceMonoColumnRuns(rows, medianHeight * ContinuationPitchScale);
        if (rows.Count < MinRows)
        {
            return null;
        }

        // 5. Table-vs-multicolumn-body discriminator, computed on the MERGED rows (#329 review): a real table
        // whose cell wraps across several single-column continuation lines must not be diluted below the ratio
        // by those continuation lines — the fold in step 4 has already collapsed them into their parent row.
        // A multi-column BODY layout still fails here (each of its rows lives in one column).
        var crossColumnRows = rows.Count(row => row.FilledCount >= 2);
        if ((double)crossColumnRows / rows.Count < MinCrossColumnRowRatio)
        {
            return null;
        }

        // 6. Fill ratio: reject a sparse scatter that lands in a few bands but isn't a real table.
        var filled = rows.Sum(row => row.FilledCount);
        if ((double)filled / (rows.Count * columns.Count) < MinFillRatio)
        {
            return null;
        }

        // 7. Bullet list, not a table: a left column made entirely of list-bullet glyphs (•, ・, ‣, …) is a
        // bullet list — "marker | item text" — that happens to be a clean 2-column grid geometrically. Render it
        // as a real Markdown list instead of a table (and instead of letting it fall back to the paragraph path,
        // where the column-wise reading order would strand every bullet away from its text). Only the leading
        // column is checked — that is where a list marker sits.
        if (IsBulletMarkerColumn(rows, 0))
        {
            return RenderBulletList(rows);
        }

        return RenderGrid(rows);
    }

    /// <summary>
    /// Splits the fragments into column bands. A flat x-projection of every fragment merges two columns whenever
    /// a single wide cell reaches across their gutter in even one row (#446: a 6-column bank statement whose
    /// 日付/摘要 and an empty メモ column collapsed into their neighbours, content bleeding across cells). Two
    /// complementary, both merge-averse column models are computed and the one that reveals <b>more</b> columns
    /// is used — a merged column is the defect, so the finer partition is the safer choice, and the downstream
    /// grid tests (cross-column-row ratio, fill ratio) reject an over-split scatter as non-lossy paragraphs.
    /// <list type="bullet">
    /// <item><b>Header anchor</b> (<see cref="HeaderAnchorCuts"/>): the top row — the header, after the caller
    /// peeled any title/intro rows — usually shows every column cleanly with wide label gutters, including a
    /// sparse or all-blank column (an empty メモ) and columns whose DATA gutters are too tight to resolve
    /// (right-aligned numerics sitting ~a point apart). Each cell files into the last column whose start is left
    /// of its centre.</item>
    /// <item><b>Coverage sweep</b> (<see cref="CoverageSweepCuts"/>): the vertical whitespace streets that stay
    /// empty across (almost) every row — robust when there is no clean header but the gutters are consistent, and
    /// tolerant of a wide cell bridging a gutter in a minority of rows.</item>
    /// </list>
    /// Each returned band is the <c>[left, right]</c> extent of the fragments whose centre falls in it — matching
    /// <see cref="ColumnOf"/>. No cut from either model → one band → the caller rejects it for having &lt; 2
    /// columns (a single-column paragraph, or a table too tight to separate).
    /// </summary>
    private static List<(double Left, double Right)> DetectColumnBands(
        IReadOnlyList<List<Cell>> visualLines, double gutterThreshold)
    {
        var allCells = visualLines.SelectMany(line => line).ToList();
        var x0 = allCells.Min(c => Math.Min(c.Bounds.Left, c.Bounds.Right));
        var x1 = allCells.Max(c => Math.Max(c.Bounds.Left, c.Bounds.Right));

        var headerCuts = HeaderAnchorCuts(visualLines[0], gutterThreshold);
        var sweepCuts = CoverageSweepCuts(visualLines, gutterThreshold, x0, x1);
        var cuts = headerCuts.Count > sweepCuts.Count ? headerCuts : sweepCuts;

        // Assign each fragment to the column left of its centre; tighten each band to the fragments it holds.
        var bands = new SortedDictionary<int, (double Left, double Right)>();
        foreach (var cell in allCells)
        {
            var l = Math.Min(cell.Bounds.Left, cell.Bounds.Right);
            var r = Math.Max(cell.Bounds.Left, cell.Bounds.Right);
            var index = CountBelow(cuts, (l + r) / 2.0);
            bands[index] = bands.TryGetValue(index, out var ext)
                ? (Math.Min(ext.Left, l), Math.Max(ext.Right, r))
                : (l, r);
        }

        return bands.Values.ToList();
    }

    /// <summary>
    /// Column cuts anchored on the header row: reduces <paramref name="headerRow"/> to its clean column spans
    /// (cells left-to-right, merging any two separated by less than <paramref name="gutterThreshold"/>) and
    /// returns the left edge of every span after the first — the x where each new column begins. A cell then
    /// belongs to the last column whose start is left of its centre (see <see cref="CountBelow"/>), which files a
    /// column's content into it whatever its width (a wide wrapped description, a right-aligned number) as long
    /// as it starts before the NEXT column's header. Returns an empty list when the row is a single span (no
    /// interior column gutter), so the caller falls back to the coverage sweep.
    /// </summary>
    private static List<double> HeaderAnchorCuts(IReadOnlyList<Cell> headerRow, double gutterThreshold)
    {
        var intervals = headerRow
            .Select(c => (Left: Math.Min(c.Bounds.Left, c.Bounds.Right), Right: Math.Max(c.Bounds.Left, c.Bounds.Right)))
            .OrderBy(iv => iv.Left)
            .ToList();

        var cuts = new List<double>();
        var right = intervals[0].Right;
        for (var i = 1; i < intervals.Count; i++)
        {
            if (intervals[i].Left - right > gutterThreshold)
            {
                cuts.Add(intervals[i].Left); // a new column starts here
                right = intervals[i].Right;
            }
            else
            {
                right = Math.Max(right, intervals[i].Right);
            }
        }

        return cuts;
    }

    /// <summary>
    /// Column cuts from the vertical whitespace <b>streets</b> that separate the columns across the rows.
    /// Requiring a street to be empty across (almost) every row makes a boundary robust to a wide cell reaching
    /// into a gutter in a minority of rows (#446), unlike a flat x-projection.
    /// <para>
    /// Each row is first reduced to its occupied x-spans (its cells, with sub-gutter gaps closed, so a row that
    /// bridges a gutter with a wide cell yields ONE span across it). A street is a maximal x-run at least
    /// <paramref name="gutterThreshold"/> wide whose row coverage stays within the bridge tolerance
    /// (<see cref="GutterBridgeToleranceFraction"/>) AND that lies strictly inside the content extent
    /// (<paramref name="x0"/>, <paramref name="x1"/>) — an interior gutter, never a page margin or a column's own
    /// ragged edge; its midpoint is a column cut.
    /// </para>
    /// </summary>
    private static List<double> CoverageSweepCuts(
        IReadOnlyList<List<Cell>> visualLines, double gutterThreshold, double x0, double x1)
    {
        var spans = new List<(double Left, double Right)>();
        foreach (var line in visualLines)
        {
            var intervals = line
                .Select(c => (Left: Math.Min(c.Bounds.Left, c.Bounds.Right), Right: Math.Max(c.Bounds.Left, c.Bounds.Right)))
                .OrderBy(iv => iv.Left)
                .ToList();

            var left = intervals[0].Left;
            var right = intervals[0].Right;
            for (var i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].Left - right > gutterThreshold)
                {
                    spans.Add((left, right));
                    left = intervals[i].Left;
                    right = intervals[i].Right;
                }
                else
                {
                    right = Math.Max(right, intervals[i].Right);
                }
            }

            spans.Add((left, right));
        }

        // Sweep the x-axis. On the slice between two consecutive span edges the coverage is how many rows' spans
        // cover it (# spans starting at/before the slice − # already ended). A column gutter is a maximal run of
        // slices whose coverage stays within the bridge tolerance; record the midpoint of each wide, interior run.
        var startsSorted = spans.Select(s => s.Left).OrderBy(x => x).ToList();
        var endsSorted = spans.Select(s => s.Right).OrderBy(x => x).ToList();
        var boundaries = startsSorted.Concat(endsSorted).Distinct().OrderBy(x => x).ToList();
        var maxBridge = (int)(visualLines.Count * GutterBridgeToleranceFraction);

        var cuts = new List<double>();
        double? runStart = null;
        for (var k = 0; k + 1 < boundaries.Count; k++)
        {
            var coverage = CountAtMost(startsSorted, boundaries[k]) - CountAtMost(endsSorted, boundaries[k]);
            if (coverage <= maxBridge)
            {
                runStart ??= boundaries[k];
            }
            else
            {
                TryAddColumnCut(cuts, runStart, boundaries[k], gutterThreshold, x0, x1);
                runStart = null;
            }
        }

        TryAddColumnCut(cuts, runStart, boundaries[^1], gutterThreshold, x0, x1);
        return cuts;
    }

    /// <summary>
    /// Records the midpoint of a low-coverage run <c>[runStart, runEnd]</c> as a column cut when it is a real
    /// gutter: at least <paramref name="gutterThreshold"/> wide and strictly interior to the content extent
    /// (<paramref name="x0"/>, <paramref name="x1"/>) — a run touching either end is a page margin or a column's
    /// ragged edge, not a gutter. A null <paramref name="runStart"/> (no open run) is ignored.
    /// </summary>
    private static void TryAddColumnCut(
        List<double> cuts, double? runStart, double runEnd, double gutterThreshold, double x0, double x1)
    {
        if (runStart is double start && runEnd - start >= gutterThreshold && start > x0 && runEnd < x1)
        {
            cuts.Add((start + runEnd) / 2.0);
        }
    }

    /// <summary>Count of ascending-sorted values <c>&lt;= value</c> (binary search).</summary>
    private static int CountAtMost(List<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Count of ascending-sorted values <c>&lt; value</c> (binary search) — a fragment's column index.</summary>
    private static int CountBelow(List<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Index of the column band whose extent contains the fragment's horizontal centre.</summary>
    private static int ColumnOf(Cell cell, List<(double Left, double Right)> bands)
    {
        var centre = (cell.Bounds.Left + cell.Bounds.Right) / 2.0;
        for (var i = 0; i < bands.Count; i++)
        {
            if (centre >= bands[i].Left && centre <= bands[i].Right)
            {
                return i;
            }
        }

        // Unreachable: a cell's centre is the midpoint of its own x-range, which is contained in the band
        // built as the union of that range with its column-mates'. Return the last band defensively (never
        // throw on this not-supposed-to-happen path) so reconstruction stays non-lossy (#329 review).
        return bands.Count - 1;
    }

    /// <summary>
    /// Clusters fragments into visual lines (vertical-overlap clustering, top to bottom), each line's cells
    /// ordered left to right. Mirrors <see cref="PdfReadingOrder.GroupWordsIntoLines"/> but keeps the cells
    /// separate (it does not space-join them into one string, which would erase the cell boundaries the table
    /// path needs).
    /// </summary>
    private static List<List<Cell>> GroupIntoVisualLines(IReadOnlyList<Cell> cells)
    {
        // Top to bottom so a line accretes its cells in vertical order; new lines are always appended below
        // the existing ones, leaving the result ordered top to bottom.
        var ordered = cells.OrderByDescending(c => c.Bounds.Top).ToList();

        var lines = new List<List<Cell>>();
        var reference = new List<PdfRectangle>(); // last cell added to each line — the overlap reference

        foreach (var cell in ordered)
        {
            var placed = false;
            for (var i = 0; i < lines.Count; i++)
            {
                // Compare against the most-recently-added cell (not an accreted union) so a single tall glyph
                // cannot stretch the line's extent and pull a next-line cell in — same rationale as
                // GroupWordsIntoLines.
                if (PdfGeometry.VerticalOverlapRatio(reference[i], cell.Bounds) >= RowOverlapRatio)
                {
                    lines[i].Add(cell);
                    reference[i] = cell.Bounds;
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add(new List<Cell> { cell });
                reference.Add(cell.Bounds);
            }
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        }

        return lines;
    }

    /// <summary>A reconstructed table row: its cells (one per column, empty string when unfilled) and y-band.</summary>
    private sealed class GridRow
    {
        public GridRow(string[] cells, double top, double bottom)
        {
            Cells = cells;
            Top = top;
            Bottom = bottom;
        }

        public string[] Cells { get; }

        /// <summary>Highest edge of the lines that formed the row (PDF user space: larger Y = higher).</summary>
        public double Top { get; set; }

        /// <summary>Lowest edge of the lines that formed the row.</summary>
        public double Bottom { get; set; }

        public int FilledCount => Cells.Count(c => c.Length > 0);
    }

    /// <summary>
    /// Folds wrapped-cell continuation lines into their parent table row. A visual line is a continuation when
    /// it occupies a STRICT subset of the current row's columns (a wrapped cell only continues columns the row
    /// already has) AND sits within <paramref name="continuationPitch"/> below it; its text is appended to the
    /// matching columns (space-joined). Otherwise it starts a new row. Returns rectangular rows
    /// (<paramref name="columnCount"/> cells each, empty string for an unfilled cell), each carrying its y-band.
    /// </summary>
    private static List<GridRow> MergeRows(
        List<List<Cell>> visualLines,
        List<Dictionary<int, List<Cell>>> lineColumns,
        int columnCount,
        double continuationPitch)
    {
        var rows = new List<GridRow>();
        GridRow? current = null;
        HashSet<int>? currentColumns = null;

        for (var i = 0; i < visualLines.Count; i++)
        {
            var byColumn = lineColumns[i];
            var occupied = byColumn.Keys.ToHashSet();
            var lineTop = visualLines[i].Max(c => c.Bounds.Top);
            var lineBottom = visualLines[i].Min(c => c.Bounds.Bottom);

            var isContinuation =
                current != null
                // Strictly FEWER columns than the parent: a wrapped cell continues SOME of the row's columns.
                // A line filling the SAME columns is treated as a new row, never a multi-cell wrap — geometry
                // cannot tell "every cell of this row wrapped" from "the next record", and merging two real
                // rows is worse than splitting the rare all-cells-wrap case (#329 review; see class remarks).
                && occupied.Count < currentColumns!.Count
                && occupied.IsSubsetOf(currentColumns)     // and only columns the parent row already has
                && current!.Bottom - lineTop <= continuationPitch; // and tight against it (a single-line wrap)

            if (isContinuation)
            {
                foreach (var (column, list) in byColumn)
                {
                    var text = JoinLineCells(list);
                    current!.Cells[column] = current.Cells[column].Length == 0 ? text : current.Cells[column] + " " + text;
                }

                current!.Bottom = lineBottom;
                continue;
            }

            if (current != null)
            {
                rows.Add(current);
            }

            var cells = new string[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                cells[c] = string.Empty;
            }

            foreach (var (column, list) in byColumn)
            {
                cells[column] = JoinLineCells(list);
            }

            current = new GridRow(cells, lineTop, lineBottom);
            currentColumns = occupied;
        }

        if (current != null)
        {
            rows.Add(current);
        }

        return rows;
    }

    /// <summary>
    /// Re-attaches stray single-cell fragment rows to the adjacent data row whose matching cell is empty. A cell
    /// that is taller than its row's single-line siblings wraps to a second visual line whose y sits OUTSIDE the
    /// row's band, so <see cref="MergeRows"/> (which folds only a continuation tight against the row it is
    /// currently building) cannot capture it and leaves it as its own mono-column row interleaved next to the
    /// data row. For every multi-cell row this pulls in the immediately adjacent (within
    /// <paramref name="pitch"/>, above or below) mono-column rows that fill one of its empty columns, appending
    /// their text top-to-bottom, and drops the consumed fragments. A fragment whose column the data row already
    /// fills, or that sits a full row away, is left untouched — so a genuinely sparse row (the #329
    /// "<c>|  | E |</c>" cases) is preserved.
    /// </summary>
    private static void CoalesceStrayCellFragments(List<GridRow> rows, double pitch)
    {
        var consumed = new bool[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            if (consumed[i] || rows[i].FilledCount < 2)
            {
                continue; // only a substantial (multi-cell) data row attracts stray fragments
            }

            var host = rows[i];
            for (var column = 0; column < host.Cells.Length; column++)
            {
                if (host.Cells[column].Length != 0)
                {
                    continue; // the magnet only fills an EMPTY cell of the host row
                }

                // Gather adjacent mono-column-`column` fragments above and below, nearest-first, while they stay
                // within the host row's band expanded by `pitch`.
                var fragments = new List<int>();
                for (var j = i - 1; j >= 0 && IsStrayFragment(rows, consumed, j, column, host, pitch); j--)
                {
                    fragments.Add(j);
                }

                fragments.Reverse(); // above fragments now top-to-bottom
                for (var j = i + 1; j < rows.Count && IsStrayFragment(rows, consumed, j, column, host, pitch); j++)
                {
                    fragments.Add(j);
                }

                foreach (var j in fragments)
                {
                    host.Cells[column] = host.Cells[column].Length == 0
                        ? rows[j].Cells[column]
                        : host.Cells[column] + " " + rows[j].Cells[column];
                    host.Top = Math.Max(host.Top, rows[j].Top);
                    host.Bottom = Math.Min(host.Bottom, rows[j].Bottom);
                    consumed[j] = true;
                }
            }
        }

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (consumed[i])
            {
                rows.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Whether row <paramref name="j"/> is an unconsumed stray fragment that can fold into <paramref name="host"/>'s
    /// <paramref name="column"/>: it fills exactly that one column and its y-band lies within the host's band
    /// expanded by <paramref name="pitch"/> (immediately adjacent, not a full row away).
    /// </summary>
    private static bool IsStrayFragment(
        List<GridRow> rows, bool[] consumed, int j, int column, GridRow host, double pitch)
    {
        if (consumed[j])
        {
            return false;
        }

        var row = rows[j];
        return row.FilledCount == 1
            && row.Cells[column].Length > 0
            && row.Bottom <= host.Top + pitch
            && row.Top >= host.Bottom - pitch;
    }

    /// <summary>
    /// Merges each maximal run of consecutive single-column rows that are tight against one another (each within
    /// <paramref name="pitch"/> of the previous) into one row, concatenating per column top-to-bottom. Such a
    /// run is one record whose cells (a short label, a wrapped value) landed on separate visual lines in
    /// different columns; a multi-cell row breaks the run, so two genuine records — which sit a full row-pitch
    /// apart, not within the continuation pitch — are never merged. Runs of length 1 are left untouched (a lone
    /// sparse cell stays its own row, preserving the #329 "<c>|  | E |</c>" behavior).
    /// </summary>
    private static void CoalesceMonoColumnRuns(List<GridRow> rows, double pitch)
    {
        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i].FilledCount != 1)
            {
                i++;
                continue;
            }

            // Extend the run while the next row is also single-column and sits within the pitch below this one.
            var end = i;
            while (end + 1 < rows.Count
                   && rows[end + 1].FilledCount == 1
                   && rows[end].Bottom - rows[end + 1].Top <= pitch)
            {
                end++;
            }

            if (end > i)
            {
                var host = rows[i];
                for (var k = i + 1; k <= end; k++)
                {
                    for (var c = 0; c < host.Cells.Length; c++)
                    {
                        if (rows[k].Cells[c].Length == 0)
                        {
                            continue;
                        }

                        host.Cells[c] = host.Cells[c].Length == 0
                            ? rows[k].Cells[c]
                            : host.Cells[c] + " " + rows[k].Cells[c];
                    }

                    host.Bottom = Math.Min(host.Bottom, rows[k].Bottom);
                }

                rows.RemoveRange(i + 1, end - i);
            }

            i++;
        }
    }

    /// <summary>
    /// Single-glyph list bullets that mark a list item, never tabular data. Deliberately excludes the ASCII
    /// <c>-</c> and <c>*</c>, which appear as legitimate cell content (a dash for "none", a footnote star).
    /// </summary>
    private static readonly char[] BulletMarkers =
        { '•', '·', '・', '‣', '◦', '▪', '●', '○', '∙', '▸', '►', '∘', '⦁' };

    /// <summary>
    /// Whether column <paramref name="column"/> is a bullet-marker column: it has at least one non-empty cell and
    /// every non-empty cell is a single list-bullet glyph (see <see cref="BulletMarkers"/>). Such a column is a
    /// list marker, so the grid is a bullet list rather than a table.
    /// </summary>
    private static bool IsBulletMarkerColumn(List<GridRow> rows, int column)
    {
        var any = false;
        foreach (var row in rows)
        {
            var cell = row.Cells[column];
            if (cell.Length == 0)
            {
                continue;
            }

            var trimmed = cell.Trim();
            if (trimmed.Length != 1 || Array.IndexOf(BulletMarkers, trimmed[0]) < 0)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Renders the rows as a GFM bullet list, one <c>- item</c> per row, where the item text is the row's
    /// non-marker columns joined left to right (the leading column is the bullet glyph and is dropped). Item
    /// text is source text, so it is inline-escaped like the paragraph path (#320); a row with no text beyond
    /// the marker is skipped.
    /// </summary>
    private static string RenderBulletList(List<GridRow> rows)
    {
        var items = new List<string>();
        foreach (var row in rows)
        {
            var text = string.Join(" ", row.Cells.Skip(1).Where(c => c.Length > 0));
            if (text.Length == 0)
            {
                continue;
            }

            items.Add("- " + MarkdownText.EscapeInline(text));
        }

        return string.Join("\n", items);
    }

    /// <summary>Joins the fragments that landed in one cell of one visual line, left to right, with a single space.</summary>
    private static string JoinLineCells(List<Cell> cells)
        => string.Join(" ", cells.OrderBy(c => c.Bounds.Left).Select(c => c.Text));

    /// <summary>
    /// Renders the rectangular grid as a GFM Markdown table via <see cref="MarkdownText.RenderTable"/>. Cells
    /// are SOURCE TEXT (the PDF digital text layer), so each is escaped with
    /// <see cref="MarkdownText.EscapeInlineCell"/> — escaping the inline metacharacters (<c>* ` [ ] &lt;</c>)
    /// as well as the pipe/newline — so table cells get the same #320 source-text protection the paragraph
    /// path has (a plain cell-escape would leave a literal <c>*</c>/<c>[</c> in a contract cell to be re-parsed).
    /// </summary>
    private static string RenderGrid(List<GridRow> rows)
    {
        var escaped = rows
            .Select(row => (IReadOnlyList<string>)row.Cells.Select(MarkdownText.EscapeInlineCell).ToArray())
            .ToList();
        return MarkdownText.RenderTable(escaped);
    }
}

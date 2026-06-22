using System;
using System.Collections.Generic;
using Shouldly;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.Extract.Parse.Pdf;

public class PdfTableReconstruction_Tests
{
    // PdfRectangle(left, bottom, right, top); PDF origin is bottom-left (larger Y = higher on the page).
    // Cells are one PdfPig Word each. All fixtures use a 12pt-tall box so the median-height-scaled thresholds
    // (column gutter, continuation pitch) are stable and easy to reason about.
    private static PdfTableReconstruction.Cell Cell(string text, double left, double right, double centreY)
        => new(new PdfRectangle(left, centreY - 6, right, centreY + 6), text);

    [Fact]
    public void Renders_a_simple_grid_as_a_markdown_table()
    {
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Name", 50, 100, 700), Cell("Price", 200, 260, 700),
            Cell("Apple", 50, 100, 680), Cell("10", 200, 230, 680)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Name | Price |\n| --- | --- |\n| Apple | 10 |");
    }

    [Fact]
    public void Keeps_a_wrapped_cell_intact_in_its_own_cell()
    {
        // The #310 料金表 failure, abstracted: the third column's cell wraps to a second visual line whose y
        // sits between this row and the next. Phase A's flat linearization splits it across siblings; the
        // table path must keep "fresh red variety" together in its own cell.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Name", 50, 100, 700), Cell("Price", 200, 250, 700), Cell("Note", 330, 400, 700),
            Cell("Apple", 50, 100, 680), Cell("10", 200, 225, 680), Cell("fresh", 330, 370, 680), Cell("red", 375, 400, 680),
            Cell("variety", 330, 390, 666), // wrapped continuation of the Note cell (single-line pitch below)
            Cell("Pear", 50, 95, 645), Cell("20", 200, 225, 645), Cell("green", 330, 380, 645)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Name | Price | Note |\n" +
            "| --- | --- | --- |\n" +
            "| Apple | 10 | fresh red variety |\n" +
            "| Pear | 20 | green |");
    }

    [Fact]
    public void Returns_null_for_a_single_column_paragraph()
    {
        // Words flow left to right with ordinary inter-word gaps (no column gutter), so the x-projection is a
        // single band — not a table.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("The", 50, 68, 700), Cell("quick", 71, 101, 700), Cell("brown", 104, 140, 700),
            Cell("fox", 50, 68, 680), Cell("jumps", 71, 110, 680),
            Cell("over", 50, 80, 660), Cell("lazy", 83, 113, 660)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_multi_column_body_text_whose_rows_do_not_align()
    {
        // Two body columns (Phase A's target). There IS a gutter, so two column bands are found — but the
        // left and right columns' lines do not share y-bands, so almost every visual line lives in a single
        // column. The cross-column-row test rejects it and it degrades to paragraphs (non-lossy).
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Left", 50, 90, 700), Cell("para", 50, 90, 680), Cell("text", 50, 90, 660), Cell("more", 50, 90, 640),
            Cell("Right", 320, 370, 690), Cell("side", 320, 360, 670), Cell("words", 320, 375, 650)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_there_are_fewer_than_two_rows()
    {
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("A", 50, 100, 700), Cell("B", 200, 260, 700)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        PdfTableReconstruction.TryRender(Array.Empty<PdfTableReconstruction.Cell>()).ShouldBeNull();

        // Whitespace-only fragments carry no text and are dropped, leaving nothing to render.
        PdfTableReconstruction.TryRender(new List<PdfTableReconstruction.Cell>
        {
            Cell("   ", 50, 100, 700), Cell("\t", 200, 260, 700),
            Cell(" ", 50, 100, 680), Cell("", 200, 260, 680)
        }).ShouldBeNull();
    }

    [Fact]
    public void Escapes_a_pipe_so_a_cell_cannot_split_the_row()
    {
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("a|b", 50, 100, 700), Cell("x", 200, 260, 700),
            Cell("c", 50, 100, 680), Cell("y", 200, 260, 680)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| a\\|b | x |\n| --- | --- |\n| c | y |");
    }

    [Fact]
    public void Detects_a_right_aligned_numeric_column()
    {
        // The numeric column is right-aligned (its cells share a right edge, not a left edge). Column
        // detection projects x-ranges, so it bands the column regardless of alignment.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Item", 50, 90, 700), Cell("Qty", 200, 230, 700),
            Cell("Apple", 50, 100, 680), Cell("5", 220, 230, 680),
            Cell("Pear", 50, 95, 660), Cell("100", 210, 230, 660)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Item | Qty |\n| --- | --- |\n| Apple | 5 |\n| Pear | 100 |");
    }

    [Fact]
    public void Does_not_merge_a_distant_partial_line_into_the_row_above()
    {
        // A line that occupies a subset of columns but sits far below (a real, separate row that happens to
        // have empty cells) must NOT be folded as a wrapped-cell continuation — the pitch guard keeps it a
        // row of its own.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("A", 50, 100, 700), Cell("B", 200, 260, 700),
            Cell("C", 50, 100, 680), Cell("D", 200, 260, 680),
            Cell("E", 200, 260, 620) // only the 2nd column, but ~5 line-heights below row 2
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| A | B |\n| --- | --- |\n| C | D |\n|  | E |");
    }

    [Fact]
    public void Reconstructs_a_table_whose_cell_wraps_across_many_lines()
    {
        // #329 review: the cross-column-row test must run AFTER the wrapped-cell merge. A 2-column table whose
        // 2nd-column cell wraps to four continuation lines would otherwise be diluted (3 cross-column / 7
        // visual lines = 0.43 < 0.5) and wrongly rejected; folding first leaves 3 clean rows that all span 2
        // columns.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("H1", 50, 100, 700), Cell("H2", 200, 300, 700),
            Cell("a", 50, 100, 680), Cell("note", 200, 300, 680),
            Cell("w1", 200, 290, 666),
            Cell("w2", 200, 290, 652),
            Cell("w3", 200, 290, 638),
            Cell("w4", 200, 290, 624),
            Cell("b", 50, 100, 600), Cell("z", 200, 260, 600)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| H1 | H2 |\n| --- | --- |\n| a | note w1 w2 w3 w4 |\n| b | z |");
    }

    [Fact]
    public void Renders_an_all_columns_wrap_as_a_separate_row_documented_limitation()
    {
        // #329 review / class remarks (a): when EVERY cell of a row wraps to an aligned second line, plain
        // geometry cannot tell it from the next record, so the safe choice renders it as an extra row
        // (non-lossy) rather than risk merging two genuine rows. Documents the accepted behavior.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("H1", 50, 100, 700), Cell("H2", 200, 300, 700),
            Cell("alpha", 50, 100, 680), Cell("beta", 200, 300, 680),
            Cell("cont1", 50, 100, 668), Cell("cont2", 200, 300, 668) // both columns wrap, tight against row 2
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| H1 | H2 |\n| --- | --- |\n| alpha | beta |\n| cont1 | cont2 |");
    }

    [Fact]
    public void Does_not_merge_a_partial_line_about_one_row_below()
    {
        // #329 review: the continuation pitch is the bottom-to-top GAP (tightened to 0.9 median-heights), so a
        // partial-column line a full row-pitch below is a separate row, not a wrap. E's gap (12) exceeds
        // 0.9*12=10.8 here but would have merged under the old 1.35 window.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("A", 50, 100, 700), Cell("B", 200, 260, 700),
            Cell("C", 50, 100, 680), Cell("D", 200, 260, 680),
            Cell("E", 200, 260, 656) // col-2 only, ~1 row-pitch below row 2
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| A | B |\n| --- | --- |\n| C | D |\n|  | E |");
    }

    [Fact]
    public void Reattaches_a_tall_cell_whose_wrap_lines_bracket_the_data_row()
    {
        // The #310 料金表 備考 failure: a cell taller than its single-line row siblings wraps to a second visual
        // line, and the two lines sit ABOVE and BELOW the row's single-line cells (not tight under the row), so
        // the step-4 continuation fold cannot capture them and they land as two stray mono-column rows
        // interleaving the data row. The magnet pass (step 4b) must pull both fragments back into the row's
        // empty Note cell, top-to-bottom.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Item", 50, 90, 700), Cell("Qty", 150, 190, 700), Cell("Note", 250, 290, 700),
            Cell("note1", 250, 290, 668),                         // tall Note cell, upper line (above the data row)
            Cell("Apple", 50, 90, 660), Cell("5", 150, 175, 660), // data row — Note cell empty on this line
            Cell("note2", 250, 290, 652),                         // tall Note cell, lower line (below the data row)
            Cell("Pear", 50, 90, 620), Cell("9", 150, 175, 620), Cell("plain", 250, 290, 620)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Item | Qty | Note |\n" +
            "| --- | --- | --- |\n" +
            "| Apple | 5 | note1 note2 |\n" +
            "| Pear | 9 | plain |");
    }

    [Fact]
    public void Does_not_pull_a_sparse_cell_a_full_row_away_into_a_neighbor()
    {
        // The magnet only reaches an IMMEDIATELY adjacent mono-column fragment (within the continuation pitch).
        // A genuinely sparse cell a full row away from the empty-Note row stays its own row — it is not a wrapped
        // cell (the column-3-empty variant of the #329 "|  | E |" guard).
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Item", 50, 90, 700), Cell("Qty", 150, 190, 700), Cell("Note", 250, 290, 700),
            Cell("Apple", 50, 90, 660), Cell("5", 150, 175, 660), // data row, Note empty
            Cell("Pear", 50, 90, 620), Cell("9", 150, 175, 620), Cell("ok", 250, 290, 620),
            Cell("late", 250, 290, 580) // col-3 only, a full row below the Pear row — not a wrap of the Apple row
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Item | Qty | Note |\n" +
            "| --- | --- | --- |\n" +
            "| Apple | 5 |  |\n" +
            "| Pear | 9 | ok |\n" +
            "|  |  | late |");
    }

    [Fact]
    public void Merges_a_key_value_row_whose_label_and_wrapped_value_are_on_separate_lines()
    {
        // The page-1 契約目的 row: the short label sits on its own visual line in column 0, and its long value
        // wraps across the line ABOVE and the line BELOW the label, in column 1 — so no single line is a
        // multi-cell "host" for the stray-fragment magnet. The three tightly-stacked single-column lines are one
        // record and must merge into one row (label + the whole value), keeping the label bound to its value.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Party", 50, 100, 700), Cell("Acme Corporation", 200, 320, 700),

            Cell("provides", 200, 280, 666),  // value line 1 (col 1), above the label
            Cell("Purpose", 50, 110, 658),    // label (col 0), on its own line
            Cell("the services", 200, 300, 650), // value line 2 (col 1), below the label

            Cell("Domain", 50, 100, 600), Cell("example.com", 200, 290, 600)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| Party | Acme Corporation |\n" +
            "| --- | --- |\n" +
            "| Purpose | provides the services |\n" +
            "| Domain | example.com |");
    }

    [Fact]
    public void Renders_a_bullet_first_column_grid_as_a_markdown_list()
    {
        // A left column of list-bullet glyphs ("• | item text") is a bullet list, not tabular data, even though
        // it forms a clean 2-column grid. It must render as a real Markdown list, not a table.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("•", 50, 56, 700), Cell("First item", 90, 200, 700),
            Cell("•", 50, 56, 660), Cell("Second item", 90, 210, 660),
            Cell("•", 50, 56, 620), Cell("Third item", 90, 205, 620)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "- First item\n- Second item\n- Third item");
    }

    [Fact]
    public void Escapes_inline_markdown_metacharacters_in_a_cell()
    {
        // #329 review: a cell is source text; literal * [ ] < ` must be escaped (like the paragraph path,
        // #320), not just the pipe — otherwise "*a*" renders as emphasis inside the cell.
        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("*a*", 50, 100, 700), Cell("b", 200, 260, 700),
            Cell("c", 50, 100, 680), Cell("d", 200, 260, 680)
        };

        PdfTableReconstruction.TryRender(cells).ShouldBe(
            "| \\*a\\* | b |\n| --- | --- |\n| c | d |");
    }
}

using System;
using System.Collections.Generic;
using Shouldly;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

public class PdfRulingLines_Tests
{
    // PdfRectangle(left, bottom, right, top). A rule is a long, thin rectangle; a box is large in both axes.
    private static PdfRectangle HRule(double y, double x1, double x2) => new(x1, y - 0.5, x2, y + 0.5);
    private static PdfRectangle VRule(double x, double y1, double y2) => new(x - 0.5, y1, x + 0.5, y2);
    private static PdfRectangle Box(double left, double bottom, double right, double top) => new(left, bottom, right, top);

    // For lattice rendering fixtures: one PdfPig Word each, 12pt tall.
    private static PdfTableReconstruction.Cell Cell(string text, double left, double right, double centreY)
        => new(new PdfRectangle(left, centreY - 6, right, centreY + 6), text);

    [Fact]
    public void Detects_a_drawn_grid_of_columns_and_rows()
    {
        // 4 vertical rules (3 columns) x 3 horizontal rules (2 rows).
        var bounds = new List<PdfRectangle>
        {
            VRule(50, 100, 300), VRule(150, 100, 300), VRule(250, 100, 300), VRule(350, 100, 300),
            HRule(100, 50, 350), HRule(200, 50, 350), HRule(300, 50, 350)
        };

        var grids = PdfRulingLines.DetectGrids(bounds);

        grids.Count.ShouldBe(1);
        grids[0].ColumnCount.ShouldBe(3);
        grids[0].RowCount.ShouldBe(2);
        grids[0].ColumnBoundaries.ShouldBe(new[] { 50.0, 150, 250, 350 });
        grids[0].RowBoundaries.ShouldBe(new[] { 100.0, 200, 300 });
    }

    [Fact]
    public void Merges_double_stroked_rules_into_one_boundary()
    {
        // Each rule is drawn twice, ~0.8pt apart (a common encoding). They must collapse to one boundary each —
        // three distinct positions per axis, not six.
        var bounds = new List<PdfRectangle>
        {
            VRule(50, 100, 300), VRule(50.8, 100, 300), VRule(200, 100, 300), VRule(200.8, 100, 300), VRule(350, 100, 300), VRule(350.8, 100, 300),
            HRule(100, 50, 350), HRule(100.8, 50, 350), HRule(200, 50, 350), HRule(200.8, 50, 350), HRule(300, 50, 350), HRule(300.8, 50, 350)
        };

        var grids = PdfRulingLines.DetectGrids(bounds);

        grids.Count.ShouldBe(1);
        grids[0].ColumnBoundaries.Count.ShouldBe(3); // 50.4, 200.4, 350.4 — not six
        grids[0].RowBoundaries.Count.ShouldBe(3);
        grids[0].ColumnCount.ShouldBe(2);
        grids[0].RowCount.ShouldBe(2);
    }

    [Fact]
    public void Decomposes_a_stroked_box_into_its_four_sides()
    {
        // The table border is one stroked rectangle; interior rules add the middle column/row.
        var bounds = new List<PdfRectangle>
        {
            Box(50, 100, 350, 300),   // outer border -> x{50,350}, y{100,300}
            VRule(200, 100, 300),     // interior column rule
            HRule(200, 50, 350)       // interior row rule
        };

        var grids = PdfRulingLines.DetectGrids(bounds);

        grids.Count.ShouldBe(1);
        grids[0].ColumnBoundaries.ShouldBe(new[] { 50.0, 200, 350 });
        grids[0].RowBoundaries.ShouldBe(new[] { 100.0, 200, 300 });
    }

    [Fact]
    public void Separates_two_stacked_tables_into_two_grids()
    {
        // Two tables on one page, disjoint in x and y, with DIFFERENT column counts. They must NOT merge into
        // one spurious grid — the intersection grouping keeps them apart (top-to-bottom order).
        var bounds = new List<PdfRectangle>
        {
            // Top table: 2 columns x 2 rows.
            VRule(50, 500, 560), VRule(150, 500, 560), VRule(250, 500, 560),
            HRule(500, 50, 250), HRule(530, 50, 250), HRule(560, 50, 250),
            // Bottom table: 3 columns x 2 rows, off to the right.
            VRule(300, 300, 360), VRule(400, 300, 360), VRule(500, 300, 360), VRule(600, 300, 360),
            HRule(300, 300, 600), HRule(330, 300, 600), HRule(360, 300, 600)
        };

        var grids = PdfRulingLines.DetectGrids(bounds);

        grids.Count.ShouldBe(2);
        grids[0].ColumnCount.ShouldBe(2); // top table first
        grids[1].ColumnCount.ShouldBe(3);
    }

    [Fact]
    public void Returns_no_grid_without_column_rules()
    {
        // Only horizontal (row) rules — no vertical separators. Not a lattice; columns are left to the stream path.
        var bounds = new List<PdfRectangle>
        {
            HRule(100, 50, 350), HRule(200, 50, 350), HRule(300, 50, 350)
        };

        PdfRulingLines.DetectGrids(bounds).ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_short_segments_that_are_not_table_rules()
    {
        // Tick marks / underlines shorter than the min length must not create boundaries.
        var bounds = new List<PdfRectangle>
        {
            VRule(50, 100, 105), VRule(150, 100, 104), // 4-5pt tall — too short
            HRule(100, 50, 55)
        };

        PdfRulingLines.DetectGrids(bounds).ShouldBeEmpty();
    }

    [Fact]
    public void Renders_cells_against_a_drawn_grid()
    {
        // 2 columns x 2 rows. Ascending-y row boundaries: row 0 is the TOP band [200,300], row 1 is [100,200].
        var grid = new PdfRulingLines.Grid(
            new[] { 50.0, 150, 250 }, new[] { 100.0, 200, 300 });

        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("Name", 60, 100, 250), Cell("Price", 160, 210, 250), // top row (header)
            Cell("Apple", 60, 110, 150), Cell("10", 160, 185, 150)    // bottom row
        };

        PdfTableReconstruction.TryRenderLattice(cells, grid).ShouldBe(
            "| Name | Price |\n| --- | --- |\n| Apple | 10 |");
    }

    [Fact]
    public void Keeps_an_empty_drawn_column_and_ignores_fragments_outside_the_grid()
    {
        // 3 columns; the middle column has no content (a drawn-but-empty column, like メモ). A fragment whose
        // centre is outside the grid extent is not table content and is dropped.
        var grid = new PdfRulingLines.Grid(
            new[] { 50.0, 150, 250, 350 }, new[] { 100.0, 200, 300 });

        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("A", 60, 100, 250), Cell("C", 260, 300, 250),
            Cell("x", 60, 100, 150), Cell("z", 260, 300, 150),
            Cell("outside", 400, 480, 150) // right of the last boundary (350) -> ignored
        };

        PdfTableReconstruction.TryRenderLattice(cells, grid).ShouldBe(
            "| A |  | C |\n| --- | --- | --- |\n| x |  | z |");
    }

    [Fact]
    public void Joins_a_wrapped_and_multi_word_cell_in_reading_order()
    {
        // A description cell with several words on one visual line (slightly jittery tops) plus a wrapped second
        // line must read left-to-right, top-to-bottom — not by raw top then left.
        var grid = new PdfRulingLines.Grid(
            new[] { 50.0, 300, 450 }, new[] { 100.0, 200, 300 });

        var cells = new List<PdfTableReconstruction.Cell>
        {
            Cell("H", 60, 100, 250), Cell("H2", 310, 350, 250),
            // one visual line, tops jitter by ~1pt; a raw top-sort would scramble them
            new(new PdfRectangle(60, 144, 110, 156), "wire"),
            new(new PdfRectangle(120, 144.6, 200, 156.6), "alpha"),
            new(new PdfRectangle(210, 143.5, 260, 155.5), "beta"),
            Cell("cont", 60, 100, 132), // wrapped continuation below
            Cell("v", 310, 350, 150)
        };

        PdfTableReconstruction.TryRenderLattice(cells, grid).ShouldBe(
            "| H | H2 |\n| --- | --- |\n| wire alpha beta cont | v |");
    }
}

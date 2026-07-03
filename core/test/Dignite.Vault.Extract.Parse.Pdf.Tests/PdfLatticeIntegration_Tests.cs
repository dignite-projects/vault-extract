using System;
using System.Linq;
using Shouldly;
using UglyToad.PdfPig;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// End-to-end tests for the #450 lattice path through <see cref="PdfReadingOrder.RenderPage"/> — a PDF whose
/// table draws its grid with vector rules is reconstructed from that grid, exactly as <c>PdfExtractor</c> drives
/// it (words + the page's ruling-line bounds).
/// </summary>
public class PdfLatticeIntegration_Tests
{
    private static string RenderFirstPage(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();
        var rulingBounds = page.Paths
            .SelectMany(p => p)
            .Select(sp => sp.GetBoundingRectangle())
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .ToList();

        return PdfReadingOrder.RenderPage(
            words, Array.Empty<PdfReadingOrder.Figure>(), true, PdfHeadingScale.Build(words), rulingBounds, out _);
    }

    [Fact]
    public void Reconstructs_a_ruled_table_from_its_drawn_grid()
    {
        // A 3-column x 2-row table whose grid is DRAWN. The middle column is empty on the data row — a sparse
        // column the whitespace path could drop — but the drawn grid keeps all three columns.
        var pdf = PdfFixtures.BuildWithRules(
            texts: new[]
            {
                ("Name", 60.0, 635.0), ("Qty", 160.0, 635.0), ("Note", 260.0, 635.0), // header row (band 630..660)
                ("Apple", 60.0, 605.0), ("red", 260.0, 605.0)                          // data row (band 600..630); Qty empty
            },
            verticalRules: new[]
            {
                (50.0, 600.0, 660.0), (150.0, 600.0, 660.0), (250.0, 600.0, 660.0), (350.0, 600.0, 660.0)
            },
            horizontalRules: new[]
            {
                (600.0, 50.0, 350.0), (630.0, 50.0, 350.0), (660.0, 50.0, 350.0)
            });

        RenderFirstPage(pdf).ShouldBe(
            "| Name | Qty | Note |\n| --- | --- | --- |\n| Apple |  | red |");
    }

    [Fact]
    public void Reconstructs_two_ruled_tables_on_one_page_as_two_markdown_tables()
    {
        // #450 edge case: two stacked ruled tables with DIFFERENT column counts must be reconstructed as two
        // separate tables (top-to-bottom), not merged into one spurious grid.
        var pdf = PdfFixtures.BuildWithRules(
            texts: new[]
            {
                // Top table (2 columns), band y 630..660 (header) / 600..630 (data).
                ("A1", 60.0, 635.0), ("A2", 160.0, 635.0), ("a", 60.0, 605.0), ("b", 160.0, 605.0),
                // Bottom table (3 columns), band y 430..460 (header) / 400..430 (data).
                ("B1", 60.0, 435.0), ("B2", 160.0, 435.0), ("B3", 260.0, 435.0),
                ("x", 60.0, 405.0), ("y", 160.0, 405.0), ("z", 260.0, 405.0)
            },
            verticalRules: new[]
            {
                (50.0, 600.0, 660.0), (150.0, 600.0, 660.0), (250.0, 600.0, 660.0),          // top: 2 columns
                (50.0, 400.0, 460.0), (150.0, 400.0, 460.0), (250.0, 400.0, 460.0), (350.0, 400.0, 460.0) // bottom: 3 columns
            },
            horizontalRules: new[]
            {
                (600.0, 50.0, 250.0), (630.0, 50.0, 250.0), (660.0, 50.0, 250.0),
                (400.0, 50.0, 350.0), (430.0, 50.0, 350.0), (460.0, 50.0, 350.0)
            });

        RenderFirstPage(pdf).ShouldBe(
            "| A1 | A2 |\n| --- | --- |\n| a | b |\n\n" +
            "| B1 | B2 | B3 |\n| --- | --- | --- |\n| x | y | z |");
    }

    [Fact]
    public void Separates_columns_whose_gutter_is_too_tight_for_the_whitespace_path()
    {
        // A ruled table whose column gutter (~3pt) is narrower than the stream path's threshold and the
        // segmenter's cut — the drawn rule at x=130 is the only column signal, and the lattice path honours it.
        var pdf = PdfFixtures.BuildWithRules(
            texts: new[]
            {
                ("Left", 55.0, 635.0), ("Right", 133.0, 635.0),
                ("aaa", 55.0, 605.0), ("bbb", 133.0, 605.0)
            },
            verticalRules: new[] { (50.0, 600.0, 660.0), (130.0, 600.0, 660.0), (210.0, 600.0, 660.0) },
            horizontalRules: new[] { (600.0, 50.0, 210.0), (630.0, 50.0, 210.0), (660.0, 50.0, 210.0) });

        RenderFirstPage(pdf).ShouldBe(
            "| Left | Right |\n| --- | --- |\n| aaa | bbb |");
    }

    [Fact]
    public void Falls_back_to_the_stream_path_when_no_grid_is_drawn()
    {
        // The same table with NO ruling lines: the lattice path finds no grid and the whitespace/stream path
        // reconstructs it (all columns filled here so it reads as a clean grid).
        var pdf = PdfFixtures.BuildWithRules(
            texts: new[]
            {
                ("Name", 60.0, 635.0), ("Qty", 160.0, 635.0), ("Note", 260.0, 635.0),
                ("Apple", 60.0, 605.0), ("5", 160.0, 605.0), ("red", 260.0, 605.0)
            },
            verticalRules: Array.Empty<(double, double, double)>(),
            horizontalRules: Array.Empty<(double, double, double)>());

        RenderFirstPage(pdf).ShouldBe(
            "| Name | Qty | Note |\n| --- | --- | --- |\n| Apple | 5 | red |");
    }
}

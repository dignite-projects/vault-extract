using System;
using System.Collections.Generic;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Shouldly;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

public class PdfReadingOrder_Tests
{
    // PdfRectangle(left, bottom, right, top); PDF origin is bottom-left (larger Y = higher on the page).

    [Fact]
    public void FindNearestCaptionIndex_picks_the_closest_line_by_squared_centroid_distance()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 800, 100, 812), "far above"),
            new(new PdfRectangle(0, 400, 100, 412), "just below the image"),
            new(new PdfRectangle(0, 50, 100, 62), "far below")
        };

        var image = new PdfRectangle(0, 420, 100, 520); // centroid ≈ (50, 470)

        PdfReadingOrder.FindNearestCaptionIndex(image, lines).ShouldBe(1);
    }

    [Fact]
    public void FindNearestCaptionIndex_returns_null_when_there_are_no_lines()
    {
        PdfReadingOrder.FindNearestCaptionIndex(
            new PdfRectangle(0, 0, 10, 10),
            Array.Empty<PdfReadingOrder.TextLine>()).ShouldBeNull();
    }

    [Fact]
    public void Render_inlines_a_figure_between_text_lines_by_reading_order()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 800, 200, 812), "ALPHA"),
            new(new PdfRectangle(0, 100, 200, 112), "CHARLIE")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 400, 200, 520), "BRAVO")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        md.IndexOf("ALPHA", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("BRAVO", StringComparison.Ordinal));
        md.IndexOf("BRAVO", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("CHARLIE", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_binds_a_caption_like_line_to_the_figure_without_duplicating_it()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 800, 200, 812), "Intro paragraph"),
            new(new PdfRectangle(0, 400, 200, 412), "Figure 1: A sample chart")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 420, 200, 520), "TRANSCRIPT")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        md.ShouldContain("TRANSCRIPT");
        // Caption is consumed by the figure block and rendered once (not left in the body text too).
        CountOccurrences(md, "Figure 1: A sample chart").ShouldBe(1);
        // The caption labels the figure: it sits immediately before the transcription.
        md.IndexOf("Figure 1", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("TRANSCRIPT", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_does_not_treat_ordinary_adjacent_text_as_a_caption()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 420, 200, 432), "An ordinary sentence near the image.")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 400, 200, 418), "TRANSCRIPT")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        // The non-caption line stays in the body exactly once and is not folded into the figure label.
        CountOccurrences(md, "An ordinary sentence near the image.").ShouldBe(1);
        md.ShouldContain("TRANSCRIPT");
    }

    [Fact]
    public void Render_merges_tightly_spaced_lines_into_one_paragraph()
    {
        // Pitch (top->top) = 700-686 = 14 < 1.6 * lineHeight(12) = 19.2 -> same paragraph.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 688, 200, 700), "Wrapped line one"),
            new(new PdfRectangle(0, 674, 200, 686), "wrapped line two")
        };

        PdfReadingOrder.Render(lines, Array.Empty<PdfReadingOrder.Figure>())
            .ShouldBe("Wrapped line one wrapped line two");
    }

    [Fact]
    public void Render_splits_paragraphs_on_a_blank_line_gap()
    {
        // Pitch = 700-652 = 48 > 19.2 -> new paragraph (a single blank line between paragraphs).
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 688, 200, 700), "First paragraph"),
            new(new PdfRectangle(0, 640, 200, 652), "Second paragraph")
        };

        PdfReadingOrder.Render(lines, Array.Empty<PdfReadingOrder.Figure>())
            .ShouldBe("First paragraph\n\nSecond paragraph");
    }

    [Fact]
    public void Render_orders_a_full_width_figure_after_an_indented_line_just_above_it()
    {
        // Indented line Top=500, full-width figure Top=497 (just below). Pure top-descending order must
        // emit the line before the figure; a coarse vertical band + left tie-break would wrongly invert them.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(72, 488, 540, 500), "INDENTEDLINE")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 380, 540, 497), "FIGBELOW")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        md.IndexOf("INDENTEDLINE", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("FIGBELOW", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_binds_the_nearest_caption_like_line_past_a_closer_body_line()
    {
        // The closest line to the figure is ordinary body text; a genuine caption is slightly farther but
        // still in range. Binding must skip the nearer non-caption line and bind the caption.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 464, 200, 476), "Adjacent body text"), // centroid y=470 (nearest)
            new(new PdfRectangle(0, 434, 200, 446), "Figure 9: the chart")  // centroid y=440 (caption)
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 400, 200, 520), "CHARTDATA") // centroid y=460
        };

        var md = PdfReadingOrder.Render(lines, figures);

        md.ShouldContain("CHARTDATA");
        md.ShouldContain("Adjacent body text");
        CountOccurrences(md, "Figure 9: the chart").ShouldBe(1); // bound to the figure, not duplicated
        md.IndexOf("Figure 9", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("CHARTDATA", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_binds_a_chinese_figure_caption_in_the_common_spaceless_form()
    {
        // "图1：..." (no space after 图1) is the most common CJK caption spelling; it must bind.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 800, 200, 812), "正文段落"),
            new(new PdfRectangle(0, 400, 200, 412), "图1：销售趋势")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 420, 200, 520), "CHARTDATA")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        md.ShouldContain("CHARTDATA");
        CountOccurrences(md, "图1：销售趋势").ShouldBe(1); // bound to the figure, not duplicated in body
        md.IndexOf("图1", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("CHARTDATA", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_binds_a_japanese_figure_caption()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 400, 200, 412), "図1 概要")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 420, 200, 520), "JPFIG")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        CountOccurrences(md, "図1 概要").ShouldBe(1);
        md.IndexOf("図1", StringComparison.Ordinal)
            .ShouldBeLessThan(md.IndexOf("JPFIG", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_does_not_treat_ordinary_cjk_text_starting_with_a_label_char_as_a_caption()
    {
        // Starts with 图 but is not a caption (no figure number / colon follows) — must stay body text.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 420, 200, 432), "图书馆藏书概览")
        };
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 400, 200, 418), "T")
        };

        var md = PdfReadingOrder.Render(lines, figures);

        CountOccurrences(md, "图书馆藏书概览").ShouldBe(1); // left in the body, not folded into the figure
        md.ShouldContain("T");
    }

    [Fact]
    public void Render_escapes_a_leading_block_marker_on_a_paragraph_line()
    {
        // #320 (PDF path): a contract line like "1. Definitions" / "- clause" / "# heading" must not be
        // re-parsed as a list / heading by the downstream chunker.
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 700, 200, 712), "1. Definitions")
        };

        PdfReadingOrder.Render(lines, Array.Empty<PdfReadingOrder.Figure>())
            .ShouldBe("1\\. Definitions");
    }

    [Fact]
    public void Render_escapes_inline_metacharacters_in_a_text_layer_line()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 700, 200, 712), "see [ref](http://x) and *note*")
        };

        PdfReadingOrder.Render(lines, Array.Empty<PdfReadingOrder.Figure>())
            .ShouldBe("see \\[ref\\](http://x) and \\*note\\*");
    }

    [Fact]
    public void Render_does_not_escape_the_ocr_transcription_of_a_figure()
    {
        // A figure transcription is OCR-provider Markdown (here, a table); it is intentional structure and
        // must be emitted verbatim, never escaped. #371: it is now bracketed by the salted [Image OCR]…[End OCR]
        // sentinels (#376; no page anchor here — the Figure carries no page number), but the transcription is verbatim.
        var figures = new List<PdfReadingOrder.Figure>
        {
            new(new PdfRectangle(0, 400, 200, 520), "| a | b |\n| --- | --- |\n| 1 | 2 |")
        };

        PdfReadingOrder.Render(Array.Empty<PdfReadingOrder.TextLine>(), figures)
            .ShouldBe(ImageOcrMarkup.OpenMarker + "\n| a | b |\n| --- | --- |\n| 1 | 2 |\n" + ImageOcrMarkup.CloseMarker);
    }

    [Fact]
    public void Render_does_not_over_escape_ordinary_text()
    {
        var lines = new List<PdfReadingOrder.TextLine>
        {
            new(new PdfRectangle(0, 700, 200, 712), "Clause 3.14 applies - see above")
        };

        PdfReadingOrder.Render(lines, Array.Empty<PdfReadingOrder.Figure>())
            .ShouldBe("Clause 3.14 applies - see above");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

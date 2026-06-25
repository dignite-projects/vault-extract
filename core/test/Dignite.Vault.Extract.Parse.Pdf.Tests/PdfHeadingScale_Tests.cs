using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

public class PdfHeadingScale_Tests
{
    [Fact]
    public void Ranks_distinct_larger_sizes_as_heading_levels()
    {
        // The contract's size profile: body 11 wins on character count; the distinct larger sizes 18 and 12 are
        // ranked H1 and H2 (rank of distinct sizes, not a fixed multiplier).
        var scale = PdfHeadingScale.FromSizeCounts(new[] { (11, 1000L), (10, 200L), (12, 60L), (18, 30L) });

        scale.HasHeadings.ShouldBeTrue();
        scale.ClassifyLine(18, bold: false).ShouldBe(1);  // clearly larger than body → H1 (no bold needed)
        scale.ClassifyLine(12, bold: true).ShouldBe(2);   // only 1pt above body, but bold → H2
        scale.ClassifyLine(12, bold: false).ShouldBe(0);  // 1pt above body and NOT bold → body (weak-gap guard)
        scale.ClassifyLine(11, bold: true).ShouldBe(0);   // body size
        scale.ClassifyLine(10, bold: true).ShouldBe(0);   // below body
    }

    [Fact]
    public void A_single_size_document_has_no_headings()
    {
        var scale = PdfHeadingScale.FromSizeCounts(new[] { (11, 500L) });

        scale.HasHeadings.ShouldBeFalse();
        scale.ClassifyLine(11, bold: true).ShouldBe(0);
    }

    [Fact]
    public void Caps_heading_levels_at_six_and_drops_the_rest_to_body()
    {
        // Body 10; eight larger sizes — only the top six become headings, the smaller extras fall back to body.
        var scale = PdfHeadingScale.FromSizeCounts(new[]
        {
            (10, 1000L), (11, 1L), (12, 1L), (13, 1L), (14, 1L), (15, 1L), (16, 1L), (17, 1L), (18, 1L)
        });

        scale.ClassifyLine(18, bold: false).ShouldBe(1);  // largest → H1
        scale.ClassifyLine(13, bold: false).ShouldBe(6);  // sixth largest (18,17,16,15,14,13) → H6
        scale.ClassifyLine(12, bold: false).ShouldBe(0);  // beyond the top six → body
        scale.ClassifyLine(11, bold: false).ShouldBe(0);
    }

    [Fact]
    public void IsBoldFont_reads_the_weight_from_the_font_name()
    {
        PdfHeadingScale.IsBoldFont("BCDGEE+YuGothic-Bold").ShouldBeTrue();
        PdfHeadingScale.IsBoldFont("Helvetica-Bold").ShouldBeTrue();
        PdfHeadingScale.IsBoldFont("BCDEEE+YuGothic-Regular").ShouldBeFalse();
        PdfHeadingScale.IsBoldFont(null).ShouldBeFalse();
    }

    [Fact]
    public void IsItalicFont_reads_the_slant_from_the_font_name()
    {
        PdfHeadingScale.IsItalicFont("Times-Italic").ShouldBeTrue();
        PdfHeadingScale.IsItalicFont("Helvetica-Oblique").ShouldBeTrue();
        PdfHeadingScale.IsItalicFont("BCDEEE+YuGothic-Regular").ShouldBeFalse();
        PdfHeadingScale.IsItalicFont(null).ShouldBeFalse();
    }
}

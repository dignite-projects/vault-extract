using System;
using System.Collections.Generic;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dignite.Extract.Parse.Pdf;

/// <summary>
/// Builds single-page PDF fixtures in-code with PdfPig's <see cref="PdfDocumentBuilder"/>: text lines at
/// given baselines plus optional embedded PNG images at given placement rectangles. PDF user space is
/// bottom-left origin, so a larger Y is higher on the page.
/// </summary>
internal static class PdfFixtures
{
    // A4 in PDF points (1/72"). Exposed so geometry-sensitive tests size their rectangles against the same
    // page these fixtures build, instead of hardcoding the dimensions independently in another file.
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;

    public static byte[] Build(
        IReadOnlyList<(string Text, double BaselineY)> texts,
        IReadOnlyList<(byte[] Image, PdfRectangle Rect)>? images = null,
        TextRenderingMode? textRenderingMode = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);

        // Emit the text in an invisible rendering mode (Tr 3) when asked — reproduces the OCR text layer of
        // a searchable / "sandwich" scan, which is drawn invisibly over the full-page raster.
        if (textRenderingMode is { } mode)
        {
            page.SetTextRenderingMode(mode);
        }

        foreach (var (text, baselineY) in texts)
        {
            page.AddText(text, 12, new PdfPoint(50, baselineY), font);
        }

        if (images is not null)
        {
            foreach (var (image, rect) in images)
            {
                page.AddPng(image, rect);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a single-page PDF with text placed at explicit (X, baseline) positions — needed to lay out
    /// multi-column fixtures (e.g. a left-column label beside a right-column body) that the column-aware
    /// reading order (#310) must keep from interleaving.
    /// </summary>
    public static byte[] BuildPositioned(
        IReadOnlyList<(string Text, double X, double BaselineY)> texts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);

        foreach (var (text, x, baselineY) in texts)
        {
            page.AddText(text, 12, new PdfPoint(x, baselineY), font);
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a single-page PDF with text at explicit (X, baseline) positions, each with its own font size and
    /// weight — needed to exercise font-size → heading detection (#403). Bold uses Helvetica-Bold (font name
    /// contains "Bold", which the heading detector reads as the weight signal).
    /// </summary>
    public static byte[] BuildStyled(
        IReadOnlyList<(string Text, double X, double BaselineY, double FontSize, bool Bold)> texts)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(PageWidth, PageHeight);

        foreach (var (text, x, baselineY, fontSize, isBold) in texts)
        {
            page.AddText(text, fontSize, new PdfPoint(x, baselineY), isBold ? bold : regular);
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a single-page PDF from styled <b>lines</b>, where each line is a sequence of runs that each carry
    /// their own weight and slant — needed to exercise inline emphasis (#403): a bold or italic run *inside* a body
    /// line becomes <c>**…**</c> / <c>_…_</c>. Runs are auto-flowed left-to-right at the line's baseline, each
    /// placed immediately after the previous run's pen position plus one space, so they read as a single continuous
    /// line (a manual X gap wide enough to look like a column gutter would otherwise be split apart by the page
    /// segmentation). Bold uses Helvetica-Bold, italic Helvetica-Oblique, both Helvetica-BoldOblique (the PostScript
    /// names carry "Bold"/"Oblique", the run classifier's weight / slant signals).
    /// </summary>
    public static byte[] BuildStyledLines(
        IReadOnlyList<(double BaselineY, double FontSize, IReadOnlyList<(string Text, bool Bold, bool Italic)> Runs)> lines)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var italic = builder.AddStandard14Font(Standard14Font.HelveticaOblique);
        var boldItalic = builder.AddStandard14Font(Standard14Font.HelveticaBoldOblique);
        var page = builder.AddPage(PageWidth, PageHeight);

        foreach (var (baselineY, fontSize, runs) in lines)
        {
            var x = 50.0;
            var spaceWidth = fontSize * 0.28; // ≈ Helvetica space advance (278/1000 em) — a word gap, not a gutter
            foreach (var (text, isBold, isItalic) in runs)
            {
                var font = (isBold, isItalic) switch
                {
                    (true, true) => boldItalic,
                    (true, false) => bold,
                    (false, true) => italic,
                    _ => regular
                };
                var letters = page.AddText(text, fontSize, new PdfPoint(x, baselineY), font);
                x = letters[letters.Count - 1].EndBaseLine.X + spaceWidth;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a multi-page PDF — one page per inner list of (Text, baselineY) lines — needed to exercise the
    /// cross-page running header/footer detection (#383), which has no signal on a single page.
    /// </summary>
    public static byte[] BuildMultiPage(
        IReadOnlyList<IReadOnlyList<(string Text, double BaselineY)>> pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var texts in pages)
        {
            var page = builder.AddPage(PageWidth, PageHeight);
            foreach (var (text, baselineY) in texts)
            {
                page.AddText(text, 12, new PdfPoint(50, baselineY), font);
            }
        }

        return builder.Build();
    }
}

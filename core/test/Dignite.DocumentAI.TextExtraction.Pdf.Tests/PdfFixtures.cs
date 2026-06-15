using System;
using System.Collections.Generic;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

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
}

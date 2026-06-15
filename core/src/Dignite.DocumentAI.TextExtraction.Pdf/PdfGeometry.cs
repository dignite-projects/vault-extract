using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Core;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Geometric helpers shared by the PDF layout reconstruction (#310): the page-level reading order
/// (<see cref="PdfReadingOrder"/>) and the table reconstructor (<see cref="PdfTableReconstruction"/>) both
/// cluster boxes into visual lines/rows by vertical overlap and scale thresholds by a median height. These
/// live here so there is one definition rather than two hand-synced copies (#329 review).
/// </summary>
internal static class PdfGeometry
{
    /// <summary>
    /// Vertical overlap of two rectangles as a fraction of the shorter one's height (0 when they do not
    /// overlap). Used to decide whether two boxes share a visual line / row.
    /// </summary>
    public static double VerticalOverlapRatio(PdfRectangle a, PdfRectangle b)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (overlap <= 0)
        {
            return 0;
        }

        var minHeight = Math.Min(a.Height, b.Height);
        return minHeight <= 0 ? 0 : overlap / minHeight;
    }

    /// <summary>The median of the positive values (0 when there are none). Non-positive values are ignored.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Core;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Detects a table's drawn ruling-line grid (#450 lattice path) from a page's vector subpath bounding
/// rectangles. When a table draws its column/row separators as lines — bank statements, forms, financial
/// reports — those lines are the <b>authoritative</b> column/row model: deterministic, unlike the whitespace
/// heuristics of <see cref="PdfTableReconstruction"/>. Returns <c>null</c> when no grid of at least 2×2 cells
/// is drawn, so the caller keeps the whitespace / stream path (a borderless table, or a table with only row
/// rules and no column separators).
/// <para>
/// Robust to the two common encodings and their quirks: a ruling can be a thin filled rectangle OR a stroked
/// line; a table border can be one stroked rectangle (contributing its four sides); and rulings are frequently
/// <b>double-stroked</b> (two near-coincident segments ~a point apart). Every qualifying subpath contributes
/// candidate boundary positions, and near-coincident candidates are clustered into a single boundary.
/// </para>
/// </summary>
internal static class PdfRulingLines
{
    /// <summary>
    /// The drawn grid: the ascending x of the vertical rules (column separators) and ascending y of the
    /// horizontal rules (row separators). N boundaries delimit N-1 cells along that axis.
    /// </summary>
    public readonly record struct Grid(IReadOnlyList<double> ColumnBoundaries, IReadOnlyList<double> RowBoundaries)
    {
        public int ColumnCount => ColumnBoundaries.Count - 1;

        public int RowCount => RowBoundaries.Count - 1;
    }

    /// <summary>
    /// Detects the ruling grid from the page's subpath bounding rectangles, or <c>null</c> when fewer than a
    /// 2×2 grid of rules is present. <paramref name="minRuleLength"/> filters tick marks / glyph strokes,
    /// <paramref name="maxRuleThickness"/> separates a thin rule from a filled box, and
    /// <paramref name="clusterTolerance"/> collapses double-stroked / near-coincident rules.
    /// </summary>
    public static Grid? DetectGrid(
        IReadOnlyList<PdfRectangle> subpathBounds,
        double minRuleLength = 18.0,
        double maxRuleThickness = 3.0,
        double clusterTolerance = 3.0)
    {
        var verticalX = new List<double>();
        var horizontalY = new List<double>();

        foreach (var rect in subpathBounds)
        {
            var left = Math.Min(rect.Left, rect.Right);
            var right = Math.Max(rect.Left, rect.Right);
            var bottom = Math.Min(rect.Bottom, rect.Top);
            var top = Math.Max(rect.Bottom, rect.Top);
            var width = right - left;
            var height = top - bottom;

            if (width >= minRuleLength && height <= maxRuleThickness)
            {
                // A long, thin horizontal segment — one row rule at its centre line.
                horizontalY.Add((top + bottom) / 2.0);
            }
            else if (height >= minRuleLength && width <= maxRuleThickness)
            {
                // A long, thin vertical segment — one column rule at its centre line.
                verticalX.Add((left + right) / 2.0);
            }
            else if (width >= minRuleLength && height >= minRuleLength)
            {
                // A large rectangle (a stroked table/cell border, or a shaded fill): contribute its four sides
                // as candidate rules. Spurious sides from a fill fall out in clustering / the grid-shape check.
                horizontalY.Add(top);
                horizontalY.Add(bottom);
                verticalX.Add(left);
                verticalX.Add(right);
            }
        }

        var columns = Cluster(verticalX, clusterTolerance);
        var rows = Cluster(horizontalY, clusterTolerance);

        // A lattice needs >= 2 columns AND >= 2 rows (>= 3 boundaries on each axis). A page with only row rules
        // (no vertical separators) is not a lattice here; the caller's whitespace path owns columns then.
        if (columns.Count < 3 || rows.Count < 3)
        {
            return null;
        }

        return new Grid(columns, rows);
    }

    /// <summary>
    /// Merges ascending positions that sit within <paramref name="tolerance"/> of the running cluster into one
    /// boundary (the cluster mean), collapsing double-stroked / near-coincident rules. Returns the boundaries
    /// ascending. A real column gutter / row pitch is tens of points, far above the tolerance, so distinct
    /// boundaries never merge.
    /// </summary>
    private static List<double> Cluster(List<double> values, double tolerance)
    {
        var result = new List<double>();
        if (values.Count == 0)
        {
            return result;
        }

        values.Sort();
        var clusterStart = values[0];
        var sum = values[0];
        var count = 1;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] - clusterStart <= tolerance)
            {
                sum += values[i];
                count++;
            }
            else
            {
                result.Add(sum / count);
                clusterStart = values[i];
                sum = values[i];
                count = 1;
            }
        }

        result.Add(sum / count);
        return result;
    }
}

using System.Collections.Generic;
using System.Linq;
using D = DocumentFormat.OpenXml.Drawing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Renders a native DrawingML table (<c>a:tbl</c>, carried by a <c>GraphicFrame</c>) as a Markdown table.
/// This is pure structured extraction (no OCR / no vision / no LLM); it exists because <see cref="PptxExtractor"/>
/// owns the whole <c>.pptx</c> pass, so a table's text would otherwise be lost entirely. The first row is
/// treated as the header.
/// </summary>
internal static class DrawingTableRenderer
{
    public static string? Render(D.Table table)
    {
        var rows = table
            .Elements<D.TableRow>()
            .Select(row => row.Elements<D.TableCell>().Select(CellText).ToList())
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        // The Markdown table must be rectangular: the separator and every row use the WIDEST row's column
        // count, padding short rows with empty cells. A ragged table (e.g. a merged single-cell title row
        // over multi-column data) would otherwise emit a separator narrower than the data rows and render
        // as a broken grid.
        var columnCount = rows.Max(r => r.Count);
        var hasContent = rows.Any(r => r.Any(cell => cell.Length > 0));
        if (columnCount == 0 || !hasContent)
        {
            // An all-empty grid (e.g. a layout table used for spacing) carries no text — drop it.
            return null;
        }

        return MarkdownText.RenderTable(rows);
    }

    private static string CellText(D.TableCell cell)
        // Inline-escape + table-break-escape source text (#329, matching the PDF table path) so a literal
        // '*' / '[' / '`' in a cell is not re-parsed as Markdown.
        => MarkdownText.EscapeInlineCell(string.Join(" ", cell.Descendants<D.Text>().Select(t => t.InnerText)));
}

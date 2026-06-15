using System.Collections.Generic;
using System.Linq;
using System.Text;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Renders a WordprocessingML table (<c>w:tbl</c>) as a Markdown table. Pure structured extraction (no OCR
/// / vision / LLM); it exists because <see cref="DocxExtractor"/> owns the whole <c>.docx</c> pass, so a
/// table's text would otherwise be lost. The first row is treated as the header.
/// <para>
/// Merged-cell span geometry (<c>w:gridSpan</c> / <c>w:vMerge</c>) is <b>not</b> modeled — cell text is
/// kept, geometry is ignored (#308 decision: accepted blind spot). The table is padded to a rectangle using
/// the widest row's column count so a ragged row set still renders a valid grid (mirrors
/// <see cref="DrawingTableRenderer"/>). Deeply nested tables render only their host cell's direct-paragraph
/// text this step (a nested <c>w:tbl</c> inside a cell is not recursed into).
/// </para>
/// </summary>
internal static class WordTableRenderer
{
    public static string? Render(W.Table table)
    {
        var rows = table
            .Elements<W.TableRow>()
            .Select(row => row.Elements<W.TableCell>().Select(CellText).ToList())
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        // The Markdown table must be rectangular: the separator and every row use the WIDEST row's column
        // count, padding short rows with empty cells. A ragged table (e.g. a row whose cells were merged
        // via gridSpan, leaving fewer w:tc) would otherwise emit a separator narrower than the data rows
        // and render as a broken grid.
        var columnCount = rows.Max(r => r.Count);
        var hasContent = rows.Any(r => r.Any(cell => cell.Length > 0));
        if (columnCount == 0 || !hasContent)
        {
            // An all-empty grid (e.g. a layout table used for spacing) carries no text — drop it.
            return null;
        }

        return MarkdownText.RenderTable(rows);
    }

    private static string CellText(W.TableCell cell)
    {
        // Join the cell's paragraphs' text with a space — a Markdown table cell can't contain a newline — and
        // escape inline metacharacters + table breakers once (#329, matching the PDF table path so a literal
        // '*' / '[' / '`' in source text is not re-parsed as emphasis / link / code inside the cell). Use
        // Descendants (not Elements) so paragraphs wrapped in a content control (w:sdt) or custom-XML inside
        // the cell are still picked up; but skip paragraphs that belong to a NESTED table (a nested w:tbl's
        // text is an accepted blind spot and must not bleed into this cell's own text).
        var paragraphs = cell
            .Descendants<W.Paragraph>()
            .Where(p => !IsInNestedTable(p, cell))
            .Select(ParagraphPlainText)
            .Where(text => text.Length > 0);
        return MarkdownText.EscapeInlineCell(string.Join(" ", paragraphs));
    }

    /// <summary>
    /// Whether a paragraph belongs to a table nested inside <paramref name="cell"/> (a <c>w:tbl</c> sits
    /// between the paragraph and the cell) rather than to the cell directly or via a content-control wrapper.
    /// </summary>
    private static bool IsInNestedTable(W.Paragraph paragraph, W.TableCell cell)
    {
        foreach (var ancestor in paragraph.Ancestors())
        {
            if (ReferenceEquals(ancestor, cell))
            {
                return false;
            }

            if (ancestor is W.Table)
            {
                return true;
            }
        }

        return false;
    }

    private static string ParagraphPlainText(W.Paragraph paragraph)
    {
        // Cell text is rendered as plain inline text (no bold/italic markup inside a table cell this step):
        // read w:t (delText excluded => accepted view of tracked changes, matching DocxExtractor.ParagraphText),
        // and turn tabs / line breaks into a space since a cell can't span lines.
        var sb = new StringBuilder();
        foreach (var element in paragraph.Descendants<DocumentFormat.OpenXml.OpenXmlElement>())
        {
            switch (element.LocalName)
            {
                case "t":
                    sb.Append(element.InnerText);
                    break;
                case "tab":
                case "br":
                case "cr":
                    sb.Append(' ');
                    break;
            }
        }

        return sb.ToString().Trim();
    }
}

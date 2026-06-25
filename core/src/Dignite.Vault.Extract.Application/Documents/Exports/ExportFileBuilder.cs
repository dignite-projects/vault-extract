using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export file serializer. The two known formats dispatch inline instead of introducing an
/// IExportFormatWriter provider framework (CLAUDE.md: three similar lines are better than premature
/// abstraction). Introduce a contract later if custom formats such as fixed-width / XML are needed.
/// </summary>
internal static class ExportFileBuilder
{
    public static byte[] Build(ExportFormat format, IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
        => format switch
        {
            ExportFormat.Xlsx => BuildXlsx(headers, rows),
            _ => BuildCsv(headers, rows)
        };

    private static byte[] BuildCsv(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }

        // UTF-8 BOM lets Excel correctly recognize Chinese / Japanese UTF-8 content. Without a BOM,
        // Excel may parse using the local code page and produce mojibake.
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static byte[] BuildXlsx(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Export");

        for (var c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < headers.Count; c++)
            {
                ws.Cell(r + 2, c + 1).Value = c < row.Length ? (row[c] ?? string.Empty) : string.Empty;
            }
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var neutralized = NeutralizeFormula(value);

        if (neutralized.Contains(',') || neutralized.Contains('"') || neutralized.Contains('\n') || neutralized.Contains('\r'))
        {
            return "\"" + neutralized.Replace("\"", "\"\"") + "\"";
        }

        return neutralized;
    }

    // CSV / spreadsheet formula injection defense: when a value starts with = + - @ or TAB / CR after
    // ignoring leading whitespace, prefix a single quote to stop Excel / Sheets from executing it as
    // a formula. Headers and cells all come from user-controlled text (template names / column names /
    // file names / extracted field values), and this builder adds a UTF-8 BOM for Excel, which
    // increases exposure. XLSX does not need this handling because ClosedXML writes string values as
    // explicit text cells, not formula cells.
    private static string NeutralizeFormula(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // Check the original first character to catch TAB/CR prefixes, plus the first character after
        // trim to catch = + - @ after leading spaces. Excel trims leading spaces before formula
        // parsing, so both are trigger surfaces.
        var trimmed = value.TrimStart();
        var dangerous = IsFormulaTrigger(value[0])
            || (trimmed.Length > 0 && IsFormulaTrigger(trimmed[0]));

        return dangerous ? "'" + value : value;
    }

    private static bool IsFormulaTrigger(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}

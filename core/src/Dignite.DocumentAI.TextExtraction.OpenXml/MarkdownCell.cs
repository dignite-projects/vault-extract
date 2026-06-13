namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Escaping for a value placed inside a Markdown table cell. Shared by <see cref="ChartRenderer"/> and
/// <see cref="DrawingTableRenderer"/> so the rule is defined once.
/// </summary>
internal static class MarkdownCell
{
    /// <summary>
    /// Escapes a cell value so it cannot break the surrounding Markdown table: backslash first (so the
    /// pipe-escape we add next is not itself neutralized by a pre-existing trailing backslash), then the
    /// pipe column separator, and finally collapse any CR/LF to a space (a literal newline would split
    /// the row).
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Fast path: the overwhelming majority of cells contain none of the table-breaking characters, so
        // avoid the four-Replace allocation chain and just trim.
        if (text.IndexOfAny(SpecialChars) < 0)
        {
            return text.Trim();
        }

        return text
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static readonly char[] SpecialChars = { '\\', '|', '\r', '\n' };

    /// <summary>
    /// Normalizes a value used as an <b>inline</b> label (a chart title, a figure caption) rendered inside
    /// a <c>**bold**</c> run: collapses every run of whitespace — including newlines that would otherwise
    /// split the bold run or leak into a table header below it — to a single space, and trims. Pipes are
    /// left as-is (an inline label is not a table row), so the displayed text stays readable.
    /// </summary>
    public static string Inline(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(" ", text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
}

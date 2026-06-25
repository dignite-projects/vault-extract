using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// The single Markdown-text escaper shared by every text-extraction provider that emits <b>source text</b>
/// into generated Markdown — the DOCX / PPTX OpenXML providers and the PDF reading-order renderer (#320).
/// Document content is literal text, not Markdown authored by us, so a paragraph that happens to begin with
/// <c>"# "</c>, <c>"- "</c>, <c>"&gt; "</c> or <c>"1. "</c> — or a run containing <c>*</c> / <c>`</c> /
/// <c>[</c> / <c>&lt;</c> — must be escaped, or it is re-parsed as a heading / list / blockquote / emphasis /
/// link and corrupts the structure the downstream RAG chunker and the built-in LLM prompts treat as a real
/// signal.
/// <para>
/// Two complementary halves, each applied at the layer where it is safe:
/// </para>
/// <list type="bullet">
/// <item><see cref="EscapeInline"/> neutralizes <b>inline</b> constructs (emphasis / code span / link /
/// autolink) anywhere in run text. A provider applies it to the <b>raw run text BEFORE</b> wrapping that text
/// in its own intended emphasis markers, so the <c>**</c> we emit is never escaped — only a literal <c>*</c>
/// in the source is.</item>
/// <item><see cref="EscapeLineStart"/> neutralizes a leading <b>block</b> marker (ATX heading / blockquote /
/// list bullet / ordered marker / thematic break / Setext underline / table-delimiter row) on a single
/// emitted line. It runs at block assembly, after inline escaping, so it only ever sees the genuine
/// start-of-line position.</item>
/// </list>
/// <para>
/// Escaping is deliberately <b>minimal</b> — only structure-significant characters at structure-significant
/// positions (a bullet <c>-</c>/<c>+</c> only when a space/tab follows, an ordered <c>N.</c> only with an
/// ASCII digit before a space) — so the Markdown stays readable for the downstream LLM. Over-escaping is its
/// own failure mode and is avoided (e.g. <c>char.IsDigit</c> / <c>char.IsWhiteSpace</c> are NOT used, because
/// they match full-width / Arabic-Indic digits and NBSP that CommonMark does not treat as markers).
/// </para>
/// </summary>
public static class MarkdownText
{
    // Inline constructs that re-interpret literal text: emphasis (* _), code span (`), link/image ([ ]),
    // autolink / raw HTML (<). Backslash is listed first so it is escaped too. Used only by the fast-path
    // detection below; the slow path hard-codes the same set (with the intraword exception for '_').
    private static readonly char[] InlineSpecials = { '\\', '`', '*', '_', '[', ']', '<' };

    // Table-cell breakers, mirroring the original MarkdownCell.Escape rule.
    private static readonly char[] CellSpecials = { '\\', '|', '\r', '\n' };

    /// <summary>
    /// Backslash-escapes the inline-significant characters (<c>\ ` * _ [ ] &lt;</c>) anywhere in <paramref
    /// name="text"/>, so literal run text cannot open an emphasis / code / link / autolink span. Leaves every
    /// other character — including newlines, which carry the soft-break structure <see cref="EscapeLineStart"/>
    /// later inspects — untouched. Apply to raw run text BEFORE wrapping it in intended emphasis markers.
    /// </summary>
    public static string EscapeInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Fast path: the overwhelming majority of runs contain no inline-significant character.
        if (text.IndexOfAny(InlineSpecials) < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            // A '_' only forms emphasis at a word boundary — CommonMark never treats an INTRAWORD underscore
            // run as emphasis. Escaping every '_' would mangle ordinary identifiers (FORM_FIELD_TEXT,
            // snake_case), which is the over-escaping the policy avoids, so escape '_' only when at least one
            // neighbour is non-alphanumeric (whitespace / punctuation / a string boundary).
            if (ch == '_')
            {
                var intraword = i > 0 && char.IsLetterOrDigit(text[i - 1])
                                && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                if (!intraword)
                {
                    sb.Append('\\');
                }

                sb.Append(ch);
                continue;
            }

            // '*' (unlike '_') DOES open emphasis intraword; ` / [ / ] open a code span / link anywhere; and
            // '<' opens an autolink (<http://…>, <a@b.com>) or raw HTML — so these are always escaped.
            if (ch == '\\' || ch == '`' || ch == '*' || ch == '[' || ch == ']' || ch == '<')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Backslash-escapes a single leading <b>block</b> marker on one line so literal text is not re-parsed as
    /// structure: an ATX heading (<c>#</c>…, 1-6 then space/tab/end), a blockquote (<c>&gt;</c>), a Setext H1
    /// underline (a line of <c>=</c>), a thematic break (≥3 <c>-</c>/<c>*</c>/<c>_</c>), a GFM table-delimiter
    /// row (only <c>-</c>/<c>:</c>/<c>|</c>), a bullet (<c>-</c>/<c>+</c>/<c>*</c> then a space/tab), or an
    /// ordered marker (<c>1.</c>/<c>1)</c> then a space/tab). Only the one significant character is escaped;
    /// the rest of the line is preserved verbatim. A non-marker line is returned unchanged.
    /// </summary>
    public static string EscapeLineStart(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        // Up to 3 leading spaces still permit a block marker; 4+ is an indented code block (not our concern).
        var start = 0;
        while (start < line.Length && start < 3 && line[start] == ' ')
        {
            start++;
        }

        if (start >= line.Length)
        {
            return line;
        }

        var rest = line.AsSpan(start);
        var c = rest[0];

        // ATX heading: 1-6 '#' followed by a Markdown space (space/tab) or end-of-line. Escaping the first
        // '#' is enough — the remainder is then no longer at line start.
        if (c == '#')
        {
            var hashes = 0;
            while (hashes < rest.Length && rest[hashes] == '#')
            {
                hashes++;
            }

            if (hashes <= 6 && (hashes == rest.Length || IsMarkdownSpace(rest[hashes])))
            {
                return EscapeAt(line, start);
            }
        }

        // Blockquote.
        if (c == '>')
        {
            return EscapeAt(line, start);
        }

        // Setext H1 underline: a line of only '=' (optional trailing space/tab). Directly below a paragraph
        // line it promotes that paragraph to an <h1>, so a soft-break continuation "Title\n===" would be
        // re-parsed as a heading. (The '-' Setext H2 underline is already defeated by the thematic-break rule.)
        if (c == '=' && IsSetextH1Underline(rest))
        {
            return EscapeAt(line, start);
        }

        // Thematic break (≥3 of the same -, *, or _, spaces allowed between) — before the bullet/table rules
        // because "---"/"***" is a break, not a bullet. Escaping the first character defeats it (and the '-'
        // Setext H2 underline).
        if ((c == '-' || c == '*' || c == '_') && IsThematicBreak(rest))
        {
            return EscapeAt(line, start);
        }

        // GFM table-delimiter row (e.g. "--- | ---", "|:--|--:|", ":---:"): only -, :, |, spaces, with at
        // least one '-' and at least one '|' or ':'. Directly below a "header | row" soft-break line it forms
        // a table. Escaping the first character defeats it. (A pure "---" already went to the thematic rule.)
        if ((c == '-' || c == ':' || c == '|') && IsTableDelimiterRow(rest))
        {
            return EscapeAt(line, start);
        }

        // Bullet list: -, +, or * immediately followed by a Markdown space/tab (a bare "-5" is NOT a list —
        // avoid over-escaping ordinary text / negative numbers).
        if ((c == '-' || c == '+' || c == '*') && rest.Length > 1 && IsMarkdownSpace(rest[1]))
        {
            return EscapeAt(line, start);
        }

        // Ordered list: 1-9 ASCII digits, then '.' or ')', then a Markdown space/tab. ASCII-only on purpose —
        // CommonMark does not recognize full-width / Arabic-Indic digits as ordered markers, so matching
        // char.IsDigit would over-escape CJK / Arabic document text ("１. " / "١. "). Escape the delimiter (a
        // backslash before a digit is meaningless); "3.14" / "12:00" stay untouched (no space after).
        if (IsAsciiDigit(c))
        {
            var digits = 0;
            while (digits < rest.Length && digits < 9 && IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits < rest.Length
                && (rest[digits] == '.' || rest[digits] == ')')
                && digits + 1 < rest.Length
                && IsMarkdownSpace(rest[digits + 1]))
            {
                return EscapeAt(line, start + digits);
            }
        }

        return line;
    }

    /// <summary>
    /// Applies <see cref="EscapeLineStart"/> to every <c>'\n'</c>-separated line of <paramref name="text"/>.
    /// Use when inline escaping has already been done at the run level (e.g. the DOCX paragraph renderer) and
    /// only the per-line leading block marker still needs neutralizing.
    /// </summary>
    public static string EscapeLineStarts(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.IndexOf('\n') < 0)
        {
            return EscapeLineStart(text);
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = EscapeLineStart(lines[i]);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Fully escapes a block of source text emitted verbatim into Markdown: inline escaping (<see
    /// cref="EscapeInline"/>) plus leading-block-marker escaping (<see cref="EscapeLineStart"/>) on every
    /// line. Use where the text is NOT wrapped in provider-generated emphasis (PDF paragraph lines, PPTX shape
    /// text, DOCX text boxes / heading text), so escaping the whole string is safe.
    /// </summary>
    public static string EscapeBlockText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Fast path for the common single-line input (e.g. a collapsed heading, a one-line PPTX paragraph or
        // PDF caption): skip the Split/Join allocation, mirroring EscapeLineStarts.
        if (text.IndexOf('\n') < 0)
        {
            return EscapeLineStart(EscapeInline(text));
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = EscapeLineStart(EscapeInline(lines[i]));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Escapes a value placed inside a Markdown <b>table cell</b> so it cannot break the surrounding table:
    /// backslash first (so the pipe-escape we add next is not neutralized by a pre-existing trailing
    /// backslash), then the pipe column separator, and finally collapse any CR/LF to a space (a literal
    /// newline would split the row). Shared by the chart / DrawingML / Word table renderers.
    /// </summary>
    public static string EscapeCell(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Fast path: the overwhelming majority of cells contain none of the table-breaking characters, so
        // avoid the four-Replace allocation chain and just trim.
        if (text.IndexOfAny(CellSpecials) < 0)
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

    /// <summary>
    /// Escapes <b>source text</b> placed inside a Markdown table cell: first the inline metacharacter set
    /// (<see cref="EscapeInline"/> — <c>\ ` * _ [ ] &lt;</c>) so a literal <c>*</c> / <c>[</c> / <c>&lt;</c> in
    /// the source cannot open emphasis / a link / an autolink inside the cell, then the pipe column separator,
    /// and finally CR/LF collapsed to a space. Use this (not <see cref="EscapeCell"/>) when the cell value is
    /// literal document text rather than an already-safe value — it gives a PDF/source table cell the same
    /// metacharacter protection the paragraph path gets from <see cref="EscapeInline"/> (#320 parity, #329).
    /// <para>
    /// The two halves compose without double-escaping: <see cref="EscapeInline"/> already backslash-escapes
    /// <c>\</c> and never touches <c>|</c>, so the subsequent pipe replace only ever escapes an original
    /// literal pipe, not a backslash that escaping just introduced.
    /// </para>
    /// </summary>
    public static string EscapeInlineCell(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return EscapeInline(text)
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    /// <summary>
    /// Renders a rectangular grid as a GFM Markdown table: the FIRST row is the header (followed by the
    /// mandated <c>| --- |</c> separator), every row padded to the widest row's column count, with the
    /// trailing newline trimmed. Cells are emitted <b>verbatim</b> — the caller must have escaped them already
    /// (<see cref="EscapeCell"/> for already-safe values, <see cref="EscapeInlineCell"/> for source text).
    /// Shared by the Word / DrawingML / chart renderers and the PDF table reconstructor so the GFM table shape
    /// lives in one place (#329). Returns the empty string for an empty / zero-column grid.
    /// </summary>
    public static string RenderTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (columnCount == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("| ");
            for (var c = 0; c < columnCount; c++)
            {
                sb.Append(c < rows[r].Count ? rows[r][c] : string.Empty);
                sb.Append(c == columnCount - 1 ? " |" : " | ");
            }

            sb.Append('\n');
            if (r == 0)
            {
                sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columnCount))).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Normalizes a value used as an <b>inline</b> label (a chart title, a figure caption) rendered inside a
    /// <c>**bold**</c> run: collapses every run of whitespace — including newlines that would otherwise split
    /// the bold run or leak into a table header below it — to a single space, and trims. Does NOT escape
    /// Markdown metacharacters; a caller that emits the label into live Markdown wraps this in <see
    /// cref="EscapeInline"/> (the label is one line, so the leading-block-marker half is not needed).
    /// </summary>
    public static string InlineLabel(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Returns <paramref name="line"/> with a single backslash inserted before index <paramref name="index"/>.</summary>
    private static string EscapeAt(string line, int index)
        => line.Substring(0, index) + "\\" + line.Substring(index);

    /// <summary>A literal space or tab — the only whitespace CommonMark accepts after a block marker.</summary>
    private static bool IsMarkdownSpace(char c) => c == ' ' || c == '\t';

    /// <summary>An ASCII digit — the only digits CommonMark recognizes in an ordered-list marker.</summary>
    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// Whether <paramref name="s"/> is a CommonMark thematic break: at least three matching <c>-</c>, <c>*</c>
    /// or <c>_</c> characters and nothing else but spaces/tabs.
    /// </summary>
    private static bool IsThematicBreak(ReadOnlySpan<char> s)
    {
        var marker = '\0';
        var count = 0;
        foreach (var ch in s)
        {
            if (ch == ' ' || ch == '\t')
            {
                continue;
            }

            if (ch != '-' && ch != '*' && ch != '_')
            {
                return false;
            }

            if (marker == '\0')
            {
                marker = ch;
            }
            else if (ch != marker)
            {
                return false;
            }

            count++;
        }

        return count >= 3;
    }

    /// <summary>Whether <paramref name="s"/> is a Setext H1 underline: one or more <c>=</c> then only spaces/tabs.</summary>
    private static bool IsSetextH1Underline(ReadOnlySpan<char> s)
    {
        var i = 0;
        while (i < s.Length && s[i] == '=')
        {
            i++;
        }

        if (i == 0)
        {
            return false;
        }

        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
        {
            i++;
        }

        return i == s.Length;
    }

    /// <summary>
    /// Whether <paramref name="s"/> looks like a GFM table-delimiter row: only <c>-</c>/<c>:</c>/<c>|</c> and
    /// spaces/tabs, with at least one <c>-</c> and at least one <c>|</c> or <c>:</c> (a pure <c>---</c> run is
    /// a thematic break, handled separately). Ordinary prose never consists solely of these characters, so the
    /// over-escape risk is negligible.
    /// </summary>
    private static bool IsTableDelimiterRow(ReadOnlySpan<char> s)
    {
        var hasDash = false;
        var hasPipeOrColon = false;
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '-':
                    hasDash = true;
                    break;
                case '|':
                case ':':
                    hasPipeOrColon = true;
                    break;
                case ' ':
                case '\t':
                    break;
                default:
                    return false;
            }
        }

        return hasDash && hasPipeOrColon;
    }
}

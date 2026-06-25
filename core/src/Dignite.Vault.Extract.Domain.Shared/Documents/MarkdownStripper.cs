using System.Text.RegularExpressions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Converts Markdown back to plain text by removing markup. This only performs syntax-level removal
/// and does not parse complex structures. Mainly used for plain-text contexts such as DTO summaries,
/// ContentLength estimates, or legacy UI surfaces that do not render Markdown.
/// </summary>
public static class MarkdownStripper
{
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var s = markdown;

        // Fenced code blocks ``` ... ```: keep inner text and remove fence lines.
        s = Regex.Replace(s, @"^```[^\n]*\n", string.Empty, RegexOptions.Multiline);
        s = Regex.Replace(s, @"\n```\s*$", string.Empty, RegexOptions.Multiline);

        // Image ![alt](url) -> alt.
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^\)]*\)", "$1");

        // Link [text](url) -> text.
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^\)]*\)", "$1");

        // Table separator rows such as |---|---|.
        s = Regex.Replace(s, @"^\s*\|?[\s:\-\|]+\|\s*$", string.Empty, RegexOptions.Multiline);

        // Table pipe characters -> spaces.
        s = s.Replace("|", " ");

        // Headings # ## ### ...
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", string.Empty, RegexOptions.Multiline);

        // Block quotes >.
        s = Regex.Replace(s, @"^\s{0,3}>\s?", string.Empty, RegexOptions.Multiline);

        // List items -, *, +, 1.
        s = Regex.Replace(s, @"^\s{0,3}([-*+]|\d+\.)\s+", string.Empty, RegexOptions.Multiline);

        // Horizontal rules ---, ***, ___.
        s = Regex.Replace(s, @"^\s{0,3}([-*_]\s*){3,}$", string.Empty, RegexOptions.Multiline);

        // Bold / italic **x**, __x__, *x*, _x_.
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
        s = Regex.Replace(s, @"(?<!\w)([*_])(.+?)\1(?!\w)", "$2");

        // Inline code `code`.
        s = Regex.Replace(s, @"`([^`]+)`", "$1");

        // Collapse excessive blank lines.
        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        return s.Trim();
    }
}

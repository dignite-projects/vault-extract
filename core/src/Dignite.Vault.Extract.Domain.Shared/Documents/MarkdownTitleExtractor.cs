using System;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Ai;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Extracts a concise display title from Markdown.
/// Priority: first ATX heading (# / ## / ...), then first non-empty plain-text paragraph.
/// Returns null when all extraction paths fail, leaving the caller to fall back deterministically to a
/// file name or similar value.
/// </summary>
public static class MarkdownTitleExtractor
{
    /// <summary>
    /// Extracts a display title. <paramref name="maxLength"/> defaults to
    /// <see cref="DocumentConsts.MaxTitleLength"/>. The returned value is trimmed, whitespace-normalized,
    /// and never longer than <paramref name="maxLength"/>; returns null when no usable text is found.
    /// </summary>
    public static string? ExtractTitle(string? markdown, int? maxLength = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var limit = maxLength ?? DocumentConsts.MaxTitleLength;
        if (limit <= 0)
        {
            return null;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // 1. Prefer ATX headings (# H1 through ###### H6). Take the first heading so documents that
        // start with H2/H3 instead of H1 are still covered.
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = Regex.Match(line, @"^#{1,6}\s+(.+?)\s*#*\s*$");
            if (match.Success)
            {
                var headingText = StripInlineMarkdown(match.Groups[1].Value);
                var normalized = Normalize(headingText, limit);
                if (!string.IsNullOrEmpty(normalized))
                {
                    return normalized;
                }
            }
        }

        // 2. Without a heading, fall back to the first non-empty text paragraph, skipping noise such
        // as fenced code blocks, table separators, and list markers.
        var inFence = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0)
            {
                continue;
            }

            // Skip table separator rows such as |---|---|.
            if (Regex.IsMatch(line, @"^\|?[\s:\-\|]+\|?$"))
            {
                continue;
            }

            // Skip horizontal rules such as ---, ***, ___.
            if (Regex.IsMatch(line, @"^([-*_]\s*){3,}$"))
            {
                continue;
            }

            // Strip prefixes: quote >, list item -/*/+, or numbered 1.
            var stripped = Regex.Replace(line, @"^>\s*", string.Empty);
            stripped = Regex.Replace(stripped, @"^([-*+]|\d+\.)\s+", string.Empty);

            var candidate = StripInlineMarkdown(stripped);
            var normalized = Normalize(candidate, limit);
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string StripInlineMarkdown(string s)
    {
        // Image ![alt](url) -> alt.
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^\)]*\)", "$1");
        // Link [text](url) -> text.
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^\)]*\)", "$1");
        // Bold / italic.
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
        s = Regex.Replace(s, @"(?<!\w)([*_])(.+?)\1(?!\w)", "$2");
        // Inline code.
        s = Regex.Replace(s, @"`([^`]+)`", "$1");
        return s;
    }

    private static string Normalize(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Collapse repeated whitespace to a single space.
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        // #491: cut at a char boundary. Pre-truncating to exactly `limit` with a raw slice would leave a lone high
        // surrogate that Document.SetTitle cannot repair — its own surrogate guard only runs on the `> MaxTitleLength`
        // branch, which an exactly-`limit` string never enters.
        return TextTruncator.AtCharBoundary(collapsed, limit);
    }
}

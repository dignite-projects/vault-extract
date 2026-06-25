using System;
using System.Globalization;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Maps a Word paragraph's style to a Markdown heading level (1-6) — the structural backbone of the DOCX
/// rebuild (#308). Resolution order:
/// <list type="number">
///   <item>the paragraph <b>style ID</b> — the built-in <c>Heading1</c>..<c>Heading9</c> (and <c>Title</c>).
///   Style IDs are stable English identifiers even when the display name is localized (e.g. German
///   "Überschrift 1" still has ID <c>Heading1</c>), so matching the ID is more robust than the name;</item>
///   <item>the explicit outline level (<c>w:outlineLvl</c>, 0-based) as a fallback for paragraphs that
///   carry an outline level without a heading style (e.g. a custom style linked to an outline level).</item>
/// </list>
/// Word supports nine heading levels; Markdown (ATX) supports six, so levels are clamped to 6.
/// </summary>
internal static class WordStyleMap
{
    private const int MaxMarkdownHeadingLevel = 6;
    private const string HeadingStylePrefix = "Heading";

    /// <returns>The Markdown heading level (1-6), or <c>null</c> when the paragraph is not a heading.</returns>
    public static int? HeadingLevel(W.Paragraph paragraph)
    {
        var properties = paragraph.ParagraphProperties;
        if (properties is null)
        {
            return null;
        }

        var styleId = properties.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrEmpty(styleId))
        {
            // "Title" maps to the top heading level.
            if (string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            // Built-in "Heading{n}" style IDs (no space; the localized display name may differ). A trailing
            // non-numeric suffix (e.g. the linked character style "Heading1Char") fails the parse and is
            // correctly NOT treated as a paragraph heading.
            if (styleId.StartsWith(HeadingStylePrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    styleId.AsSpan(HeadingStylePrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var n)
                && n >= 1)
            {
                return Math.Min(n, MaxMarkdownHeadingLevel);
            }
        }

        // Explicit outline level (0-based: 0 => H1). Word uses level 9 to mean "body text" (no outline),
        // so only 0-8 denote a heading.
        var outline = properties.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and < 9)
        {
            return Math.Min(outline.Value + 1, MaxMarkdownHeadingLevel);
        }

        return null;
    }
}

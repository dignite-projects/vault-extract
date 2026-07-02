using System;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Maps a Word paragraph's style to a Markdown heading level (1-6) — the structural backbone of the DOCX
/// rebuild (#308). Resolution order:
/// <list type="number">
///   <item>the paragraph <b>style ID</b> — the built-in <c>Heading1</c>..<c>Heading9</c> (and <c>Title</c>).
///   Style IDs are stable English identifiers even when the display name is localized (e.g. German
///   "Überschrift 1" still has ID <c>Heading1</c>), so matching the ID is more robust than the name;</item>
///   <item>the paragraph-direct outline level (<c>w:pPr/w:outlineLvl</c>, 0-based) — direct formatting that
///   overrides the style;</item>
///   <item>[#316] the paragraph's <b>custom style</b> resolved against <c>styles.xml</c>: follow the
///   <c>w:basedOn</c> chain to a built-in <c>HeadingN</c>/<c>Title</c> ancestor, or read a style-level
///   <c>w:outlineLvl</c>. This recovers a heading authored via a custom style (e.g. a house "Chapter Title"
///   style based on <c>Heading1</c>) that the paragraph-own checks miss. Requires the style part; falls back
///   to <c>null</c> when no style part / definition resolves.</item>
/// </list>
/// Word supports nine heading levels; Markdown (ATX) supports six, so levels are clamped to 6.
/// </summary>
internal static class WordStyleMap
{
    private const int MaxMarkdownHeadingLevel = 6;
    private const string HeadingStylePrefix = "Heading";

    /// <summary>
    /// Bound on the <c>w:basedOn</c> chain walk — style inheritance is shallow in practice, and the bound
    /// also stops a malformed / cyclic <c>basedOn</c> chain from looping.
    /// </summary>
    private const int MaxStyleChainDepth = 16;

    /// <summary>
    /// The Markdown heading level (1-6) for a paragraph, or <c>null</c> when it is not a heading.
    /// <paramref name="mainPart"/> is optional: when supplied, a paragraph carrying a <b>custom</b> style is
    /// resolved against <c>styles.xml</c> (the <c>basedOn</c> chain + style-level outline); when <c>null</c>,
    /// only the paragraph's own properties are considered (the pre-#316 behavior).
    /// </summary>
    public static int? HeadingLevel(W.Paragraph paragraph, MainDocumentPart? mainPart = null)
    {
        var properties = paragraph.ParagraphProperties;
        if (properties is null)
        {
            return null;
        }

        var styleId = properties.ParagraphStyleId?.Val?.Value;

        // 1. Built-in "Heading{n}" / "Title" style ID on the paragraph's own style.
        var builtIn = BuiltInHeadingLevel(styleId);
        if (builtIn is not null)
        {
            return builtIn;
        }

        // 2. Paragraph-direct outline level (0-based: 0 => H1). Word uses level 9 to mean "body text" (no
        //    outline), so only 0-8 denote a heading. Direct formatting overrides the style.
        var outline = properties.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and < 9)
        {
            return Math.Min(outline.Value + 1, MaxMarkdownHeadingLevel);
        }

        // 3. [#316] A custom style whose heading semantics live in styles.xml (based on a built-in HeadingN,
        //    or a style-level outlineLvl). Requires the style part; otherwise the paragraph is not a heading.
        if (!string.IsNullOrEmpty(styleId) && mainPart is not null)
        {
            return ResolveStyleHeadingLevel(styleId!, mainPart);
        }

        return null;
    }

    /// <summary>
    /// The heading level for a <b>built-in</b> style ID — <c>Title</c> (=&gt; 1) or <c>Heading{n}</c>
    /// (=&gt; n, clamped to 6) — or <c>null</c>. A trailing non-numeric suffix (the auto-generated linked
    /// character style <c>Heading1Char</c>) fails the parse and is correctly NOT treated as a heading.
    /// </summary>
    private static int? BuiltInHeadingLevel(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId))
        {
            return null;
        }

        // "Title" maps to the top heading level.
        if (string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // Built-in "Heading{n}" style IDs (no space; the localized display name may differ).
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

        return null;
    }

    /// <summary>
    /// Resolves a custom paragraph style against <c>styles.xml</c> by walking the <c>w:basedOn</c> chain: a
    /// built-in <c>HeadingN</c> / <c>Title</c> id reached along the chain (even a latent style with no
    /// explicit definition) supplies the level, as does the first style-level <c>w:outlineLvl</c> encountered
    /// (the closest override wins). Returns <c>null</c> when the chain resolves to no heading.
    /// </summary>
    private static int? ResolveStyleHeadingLevel(string styleId, MainDocumentPart mainPart)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return null;
        }

        var currentId = styleId;
        for (var depth = 0; depth < MaxStyleChainDepth && !string.IsNullOrEmpty(currentId); depth++)
        {
            // A built-in Heading / Title id in the chain carries the level even if that style has no explicit
            // definition (a latent built-in style) — so check the id before requiring a definition.
            var builtIn = BuiltInHeadingLevel(currentId);
            if (builtIn is not null)
            {
                return builtIn;
            }

            var style = FindParagraphStyle(styles, currentId!);
            if (style is null)
            {
                return null;
            }

            // A style-level outline level (w:style/w:pPr/w:outlineLvl).
            var outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (outline is >= 0 and < 9)
            {
                return Math.Min(outline.Value + 1, MaxMarkdownHeadingLevel);
            }

            currentId = style.BasedOn?.Val?.Value;
        }

        return null;
    }

    /// <summary>The <c>w:style</c> whose <c>w:styleId</c> matches, or <c>null</c>.</summary>
    private static W.Style? FindParagraphStyle(W.Styles styles, string styleId)
        => styles.Elements<W.Style>().FirstOrDefault(s =>
            string.Equals(s.StyleId?.Value, styleId, StringComparison.Ordinal));
}

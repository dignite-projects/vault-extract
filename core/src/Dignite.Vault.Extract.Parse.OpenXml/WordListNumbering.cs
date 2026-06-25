using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Resolves a Word paragraph's list membership (<c>w:numPr</c>) to a Markdown list marker kind + nesting
/// level by walking the numbering definitions: <c>numId</c> → the numbering instance → its
/// <c>abstractNumId</c> → the requested level's <c>w:numFmt</c>. A <c>bullet</c> format renders as
/// <c>-</c>; any other counted format (<c>decimal</c>, <c>lowerLetter</c>, <c>lowerRoman</c>, …) renders as
/// an ordered <c>1.</c> marker.
/// <para>
/// Per-instance level overrides (<c>w:lvlOverride</c>) are not applied this step — the abstract definition's
/// format is used. When the numbering part or definition cannot be resolved, the paragraph is still a list
/// item (it carries <c>w:numPr</c>), so it defaults to a bullet rather than being dropped.
/// </para>
/// </summary>
internal static class WordListNumbering
{
    /// <summary>A paragraph's list placement: zero-based nesting <paramref name="Level"/> and whether it is ordered.</summary>
    public readonly record struct ListInfo(int Level, bool Ordered);

    /// <returns>The list placement, or <c>null</c> when the paragraph is not a list item.</returns>
    public static ListInfo? Resolve(W.Paragraph paragraph, NumberingDefinitionsPart? numberingPart)
    {
        var numberingProperties = paragraph.ParagraphProperties?.NumberingProperties;
        var numId = numberingProperties?.NumberingId?.Val?.Value;

        // numId 0 is Word's "remove numbering" sentinel; an absent numPr/numId means not a list item.
        if (numId is null or 0)
        {
            return null;
        }

        var level = numberingProperties!.NumberingLevelReference?.Val?.Value ?? 0;
        var format = ResolveFormat(numId.Value, level, numberingPart);

        // An explicit "none" format means numbering is turned off for this level — treat as a normal paragraph.
        if (format == W.NumberFormatValues.None)
        {
            return null;
        }

        // An unresolved definition (no numbering part / dangling reference) still IS a list item via numPr,
        // so default it to a bullet. Any non-bullet, non-none format is an ordered list.
        var ordered = format is not null && format != W.NumberFormatValues.Bullet;
        return new ListInfo(level, ordered);
    }

    private static W.NumberFormatValues? ResolveFormat(int numId, int level, NumberingDefinitionsPart? numberingPart)
    {
        var numbering = numberingPart?.Numbering;
        if (numbering is null)
        {
            return null;
        }

        var instance = numbering.Elements<W.NumberingInstance>()
            .FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractNumId = instance?.AbstractNumId?.Val?.Value;
        if (abstractNumId is null)
        {
            return null;
        }

        var abstractNum = numbering.Elements<W.AbstractNum>()
            .FirstOrDefault(a => a.AbstractNumberId?.Value == abstractNumId);
        if (abstractNum is null)
        {
            return null;
        }

        // Use the exact requested level only. If that ilvl is absent (malformed / partial numbering) do NOT
        // fall back to another level's format — that would silently flip a bullet into an ordered list (or
        // vice versa). Returning null here lets Resolve default the item to a neutral bullet instead.
        var levelDefinition = abstractNum.Elements<W.Level>()
            .FirstOrDefault(l => (l.LevelIndex?.Value ?? 0) == level);
        return levelDefinition?.NumberingFormat?.Val?.Value;
    }
}

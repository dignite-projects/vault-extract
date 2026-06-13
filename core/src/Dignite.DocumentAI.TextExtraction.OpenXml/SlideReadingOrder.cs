using System.Collections.Generic;
using System.Linq;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Reading-order assembly for a single slide. PPTX is semantically <b>fixed-layout per slide</b>
/// (PDF-like, position-based — not flow-based), so blocks are ordered by their shape's top-left offset
/// (EMU): top-to-bottom (<see cref="SlideBlock.OrderY"/> ascending), then left-to-right
/// (<see cref="SlideBlock.OrderX"/> ascending). Unlike PDF user space, DrawingML's Y axis points
/// <b>downward</b> (origin top-left), so ascending Y is already natural reading order — no inversion.
/// <para>
/// <see cref="SlideBlock.Sequence"/> is the document-encounter order, used as the final tiebreak so
/// shapes that share a position (or carry no offset) keep their declared order, and images discovered
/// inside a grouped shape stay in group-encounter order under the group's position.
/// </para>
/// </summary>
internal static class SlideReadingOrder
{
    /// <summary>
    /// One renderable block on a slide: a text shape's Markdown, an embedded image's transcription, or a
    /// chart's data table. <see cref="OrderY"/>/<see cref="OrderX"/> are EMU offsets (top-left); a block
    /// whose shape carries no offset sorts to the front by position but keeps document order via
    /// <see cref="Sequence"/>.
    /// </summary>
    public readonly record struct SlideBlock(long OrderY, long OrderX, int Sequence, string Markdown);

    /// <summary>
    /// Orders the slide's blocks into reading order and joins them with blank lines. Returns an empty
    /// string when there is nothing renderable.
    /// </summary>
    public static string Render(IReadOnlyList<SlideBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var ordered = blocks
            .OrderBy(b => b.OrderY)
            .ThenBy(b => b.OrderX)
            .ThenBy(b => b.Sequence)
            .Select(b => b.Markdown)
            .Where(md => !string.IsNullOrWhiteSpace(md));

        return string.Join("\n\n", ordered);
    }
}

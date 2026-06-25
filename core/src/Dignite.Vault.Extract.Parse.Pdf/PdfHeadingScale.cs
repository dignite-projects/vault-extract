using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Content;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Maps a digital PDF's font sizes to Markdown heading levels (#403). Built once per document from the font-size
/// histogram of all its words, then queried per visual line.
/// <para>
/// Follows the field-standard heuristic (PyMuPDF4LLM's <c>IdentifyHeaders</c>): the body font size is the most
/// frequent rounded <see cref="Letter.PointSize"/> <b>weighted by character count</b> (body text wins by sheer
/// volume); the <b>distinct</b> sizes larger than body are ranked descending and assigned levels 1..N (largest
/// present size = H1), capped at <see cref="MaxLevels"/>. This is rank-of-distinct-sizes, not a fixed multiplier,
/// so it adapts to each document's actual size palette.
/// </para>
/// <para>
/// One refinement over the bare heuristic: because the gap between a body size and the next-larger heading size
/// can be just 1pt (a contract's 11pt body vs. 12pt section headings), a candidate that is only marginally larger
/// than body (&lt; <see cref="UnboldedHeadingMinGapPoints"/>) is accepted as a heading only when the line is also
/// <b>bold</b> — the weight corroboration called for in #403. A clearly larger size needs no such corroboration.
/// </para>
/// </summary>
internal sealed class PdfHeadingScale
{
    private const int MaxLevels = 6;

    // A candidate heading size within this many points of the body size is too weak on size alone; require bold.
    private const double UnboldedHeadingMinGapPoints = 2.0;

    private readonly int _bodyLimit;
    private readonly IReadOnlyDictionary<int, int> _levelBySize;

    private PdfHeadingScale(int bodyLimit, IReadOnlyDictionary<int, int> levelBySize)
    {
        _bodyLimit = bodyLimit;
        _levelBySize = levelBySize;
    }

    /// <summary>Whether any heading sizes were found (a document with no typographic hierarchy has none).</summary>
    public bool HasHeadings => _levelBySize.Count > 0;

    /// <summary>
    /// The Markdown heading level (1 = largest = <c>#</c>) for a visual line whose largest glyph is
    /// <paramref name="maxPointSize"/> and which is <paramref name="bold"/>, or 0 when the line is body text.
    /// </summary>
    public int ClassifyLine(double maxPointSize, bool bold)
    {
        var size = (int)Math.Round(maxPointSize);
        if (size <= _bodyLimit || !_levelBySize.TryGetValue(size, out var level))
        {
            return 0;
        }

        // A heading only marginally larger than body is a weak signal — require bold to corroborate (#403).
        if (size - _bodyLimit < UnboldedHeadingMinGapPoints && !bold)
        {
            return 0;
        }

        return level;
    }

    /// <summary>
    /// Builds the scale from all of a document's words. Returns a scale with no headings when there are no
    /// letters or no size exceeds the body size (a single-size document has no hierarchy).
    /// </summary>
    public static PdfHeadingScale Build(IEnumerable<Word> words)
    {
        var histogram = new Dictionary<int, long>(); // rounded point size -> character count
        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                var size = (int)Math.Round(letter.PointSize);
                if (size <= 0)
                {
                    continue;
                }

                histogram[size] = histogram.GetValueOrDefault(size) + 1;
            }
        }

        return FromSizeCounts(histogram.Select(kv => (kv.Key, kv.Value)));
    }

    /// <summary>
    /// Builds the scale from a rounded-size → character-count histogram (the testable core of
    /// <see cref="Build"/>). Body size = the most frequent size by character count (ties → the larger size, so
    /// body is never under-estimated); the distinct larger sizes are ranked into heading levels.
    /// </summary>
    internal static PdfHeadingScale FromSizeCounts(IEnumerable<(int Size, long Count)> counts)
    {
        var histogram = new Dictionary<int, long>();
        foreach (var (size, count) in counts)
        {
            if (size > 0 && count > 0)
            {
                histogram[size] = histogram.GetValueOrDefault(size) + count;
            }
        }

        if (histogram.Count == 0)
        {
            return new PdfHeadingScale(int.MaxValue, EmptyLevels);
        }

        // No artificial floor: the char-count weighting makes the body text the mode for the prose-dominated
        // target corpus, and a uniform-size document yields no sizes above body → no headings.
        var bodyLimit = histogram
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key)
            .First().Key;

        var headingSizes = histogram.Keys
            .Where(size => size > bodyLimit)
            .OrderByDescending(size => size)
            .Take(MaxLevels)
            .ToList();

        var levelBySize = new Dictionary<int, int>(headingSizes.Count);
        for (var i = 0; i < headingSizes.Count; i++)
        {
            levelBySize[headingSizes[i]] = i + 1; // largest present size = level 1 (#)
        }

        return new PdfHeadingScale(bodyLimit, levelBySize);
    }

    /// <summary>Whether <paramref name="fontName"/> denotes a bold face (the standard digital-PDF heuristic).</summary>
    public static bool IsBoldFont(string? fontName)
        => fontName is not null
           && (fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
               || fontName.Contains("Black", StringComparison.OrdinalIgnoreCase)
               || fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
               || fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="fontName"/> denotes an italic / oblique face.</summary>
    public static bool IsItalicFont(string? fontName)
        => fontName is not null
           && (fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase)
               || fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<int, int> EmptyLevels = new Dictionary<int, int>();
}

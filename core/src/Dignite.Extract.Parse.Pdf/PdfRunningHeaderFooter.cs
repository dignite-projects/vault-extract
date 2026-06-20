using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace Dignite.Extract.Parse.Pdf;

/// <summary>
/// Cross-page running header / footer + page-number detection for a digital PDF (#383). Such chrome
/// (a title or confidentiality line repeated at the page edge, "Page 3 of 10", "第 3 页", a bare "3")
/// is mechanical noise that pollutes the Markdown body and degrades downstream chunking / classification /
/// field extraction, so it is stripped before rendering.
/// <para>
/// <b>Mechanism — repetition, gated by position (not position alone).</b> Position only <i>selects</i>
/// candidates: the first / last <see cref="CandidateLinesPerEdge"/> visual lines of each page (the
/// header / footer band). A candidate is dropped only when its <b>digit-normalized</b> text
/// (so "第 1 页" / "第 2 页" collapse to one template, and "Page 1 of 10" matches "Page 2 of 10")
/// repeats in the <b>same band</b> across at least <see cref="MinRepeatPageFraction"/> of the pages
/// (floor <see cref="MinRepeatPageCount"/>). This is deliberately <b>not</b> "delete anything in the
/// margin band": a per-page signature block / seal / total / footnote sits in the band on a single page,
/// never repeats, and is therefore preserved — honouring the PdfPig stack's non-lossy discipline
/// (cf. <see cref="PdfExtractor.IsFullPageScanBackground"/> erring toward keeping).
/// </para>
/// <para>
/// <b>Single-page documents drop nothing</b> — there is no cross-page signal, which is the natural and
/// accepted consequence of a repetition-based detector (a lone invoice page keeps its footer).
/// </para>
/// </summary>
internal static class PdfRunningHeaderFooter
{
    /// <summary>How many visual lines at each page edge (top and bottom) are header/footer candidates.</summary>
    private const int CandidateLinesPerEdge = 3;

    /// <summary>Absolute floor on the number of pages a candidate must repeat across before it is dropped.</summary>
    private const int MinRepeatPageCount = 2;

    /// <summary>
    /// Fraction of the document's pages a candidate must repeat across (in the same band) to be treated as
    /// running chrome. With the <see cref="MinRepeatPageCount"/> floor, a 2-page document needs the line on
    /// both pages; a 10-page document needs it on at least five.
    /// </summary>
    private const double MinRepeatPageFraction = 0.5;

    // Runs of ASCII or full-width digits collapse to a single placeholder so a varying page number reads as
    // one stable template across pages ("第 1 页"/"第 2 页" -> "第 # 页", "Page 1 of 10" -> "page # of #").
    private static readonly Regex DigitRun = new("[0-9０-９]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Band tags keyed into the template so a top-of-page line and a bottom-of-page line never count as the
    // same running candidate (a header repeats at the top, a footer at the bottom).
    private const char TopBand = 'T';
    private const char BottomBand = 'B';

    /// <summary>
    /// Detects running header/footer/page-number lines across the given pages and returns, per page (by
    /// index), the set of <see cref="Word"/> instances to drop before rendering. The returned word instances
    /// are the same references found in <paramref name="pageWords"/>, so the caller filters by reference
    /// identity. Pages with nothing to drop get an empty set; a document with fewer than
    /// <see cref="MinRepeatPageCount"/> pages drops nothing.
    /// </summary>
    public static IReadOnlyList<IReadOnlySet<Word>> Detect(IReadOnlyList<IReadOnlyList<Word>> pageWords)
    {
        var pageCount = pageWords.Count;
        var result = new List<IReadOnlySet<Word>>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            result.Add(EmptyWordSet);
        }

        if (pageCount < MinRepeatPageCount)
        {
            // Single page (or empty): no cross-page repetition is possible, so nothing is running chrome.
            return result;
        }

        // Collect every edge-band candidate line, keyed by (band, normalized text), tracking which pages each
        // template appears on. A line that is both in the top and bottom band (a very short page) registers
        // under both bands.
        var candidates = new List<Candidate>();
        var pagesByKey = new Dictionary<string, HashSet<int>>();

        for (var page = 0; page < pageCount; page++)
        {
            var lines = PdfReadingOrder.GroupWordsIntoLinesWithWords(pageWords[page]);
            var lineCount = lines.Count;
            for (var i = 0; i < lineCount; i++)
            {
                var isTop = i < CandidateLinesPerEdge;
                var isBottom = i >= lineCount - CandidateLinesPerEdge;
                if (!isTop && !isBottom)
                {
                    continue;
                }

                var normalized = Normalize(lines[i].Text);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (isTop)
                {
                    Register(candidates, pagesByKey, page, TopBand, normalized, lines[i].Words);
                }

                if (isBottom)
                {
                    Register(candidates, pagesByKey, page, BottomBand, normalized, lines[i].Words);
                }
            }
        }

        var threshold = Math.Max(MinRepeatPageCount, (int)Math.Ceiling(pageCount * MinRepeatPageFraction));
        var runningKeys = pagesByKey
            .Where(kv => kv.Value.Count >= threshold)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (runningKeys.Count == 0)
        {
            return result;
        }

        // Materialize the per-page drop sets only for pages that actually have running content.
        var dropByPage = new Dictionary<int, HashSet<Word>>();
        foreach (var candidate in candidates)
        {
            if (!runningKeys.Contains(candidate.Key))
            {
                continue;
            }

            if (!dropByPage.TryGetValue(candidate.Page, out var set))
            {
                set = new HashSet<Word>();
                dropByPage[candidate.Page] = set;
            }

            foreach (var word in candidate.Words)
            {
                set.Add(word);
            }
        }

        foreach (var (page, set) in dropByPage)
        {
            result[page] = set;
        }

        return result;
    }

    private static void Register(
        List<Candidate> candidates,
        Dictionary<string, HashSet<int>> pagesByKey,
        int page,
        char band,
        string normalized,
        IReadOnlyList<Word> words)
    {
        var key = band + normalized;
        candidates.Add(new Candidate(page, key, words));
        if (!pagesByKey.TryGetValue(key, out var pages))
        {
            pages = new HashSet<int>();
            pagesByKey[key] = pages;
        }

        pages.Add(page);
    }

    /// <summary>
    /// Normalizes a candidate line into a cross-page-stable template: collapse digit runs to a placeholder
    /// (so page numbers match), collapse whitespace, trim, and lower-case (a running header keeps consistent
    /// case across pages; lower-casing is a no-op for CJK and harmless for Latin).
    /// </summary>
    private static string Normalize(string text)
    {
        var normalized = DigitRun.Replace(text, "#");
        normalized = WhitespaceRun.Replace(normalized, " ").Trim();
        return normalized.ToLowerInvariant();
    }

    private static readonly IReadOnlySet<Word> EmptyWordSet = new HashSet<Word>();

    private readonly record struct Candidate(int Page, string Key, IReadOnlyList<Word> Words);
}

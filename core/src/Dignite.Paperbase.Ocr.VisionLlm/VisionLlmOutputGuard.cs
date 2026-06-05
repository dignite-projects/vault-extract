using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Paperbase.Ocr.VisionLlm;

/// <summary>
/// Heuristic guard against vision-LLM repetition / hallucination loops.
/// <para>
/// Traditional OCR can only mis-read characters; a vision LLM driven in chat mode can additionally fall
/// into a repetition loop that fills the token budget with the same line/phrase over and over (the
/// PaddleOCR-VL chat-mode death loop in issue #259 is the reference example). As a "trusted digitization
/// channel" Paperbase must never persist such output, so the provider runs every model response through
/// this guard and discards (returns empty Markdown) on a trip.
/// </para>
/// <para>
/// Three complementary heuristics, all with deliberately conservative thresholds (the goal is to let real
/// OCR output through — including dense tables and receipts with repeated amounts — and only catch
/// egregious loops):
/// <list type="number">
///   <item>A non-empty line repeated too many times <b>consecutively</b> (multi-line loop).</item>
///   <item>A low distinct-line ratio over a large body (interleaved multi-line loop).</item>
///   <item>A single content-heavy line that is a short unit repeated many times (a no-newline char- or
///         phrase-level loop, which the line-based heuristics miss). Punctuation-only lines such as
///         Markdown table separators / horizontal rules are excluded so wide tables are not flagged.</item>
/// </list>
/// </para>
/// </summary>
public static class VisionLlmOutputGuard
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="markdown"/> looks like a repetition loop.
    /// </summary>
    /// <param name="markdown">The model's transcription output.</param>
    /// <param name="maxConsecutiveRepeatedLines">Trip when a non-empty line appears this many times in a row (i.e. this many identical consecutive lines). Values below 2 are treated as 2 — the strictest meaningful setting — never as "disabled".</param>
    /// <param name="minDistinctLineRatio">Trip if distinct/total non-empty line ratio drops below this (over a large enough body).</param>
    /// <param name="minLinesForRatioCheck">Minimum non-empty line count before the distinct-ratio heuristic applies.</param>
    /// <param name="minLengthForSegmentCheck">Minimum length of a single line before the short-period heuristic inspects it.</param>
    /// <param name="maxRepeatedSegmentLength">Largest repeating-unit (period) length the short-period heuristic treats as a loop.</param>
    /// <param name="minRepeatedSegmentRepeats">Minimum number of times the unit must tile the line to trip the short-period heuristic.</param>
    public static bool LooksLikeRepetitionLoop(
        string? markdown,
        int maxConsecutiveRepeatedLines,
        double minDistinctLineRatio,
        int minLinesForRatioCheck,
        int minLengthForSegmentCheck,
        int maxRepeatedSegmentLength,
        int minRepeatedSegmentRepeats)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return false;
        }

        var lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return false;
        }

        // Heuristic 1: a single non-empty line repeated too many times consecutively.
        // Floor the threshold at 2 so a misconfigured "1" (or 0/negative) becomes the strictest setting
        // rather than silently disabling the check. To effectively disable it, set a very large value.
        var consecutiveThreshold = Math.Max(2, maxConsecutiveRepeatedLines);
        var run = 1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i] == lines[i - 1])
            {
                run++;
                if (run >= consecutiveThreshold)
                {
                    return true;
                }
            }
            else
            {
                run = 1;
            }
        }

        // Heuristic 2: low distinct-line ratio over a large enough body (catches interleaved loops).
        if (lines.Count >= minLinesForRatioCheck)
        {
            var distinct = lines.Distinct().Count();
            var ratio = (double)distinct / lines.Count;
            if (ratio < minDistinctLineRatio)
            {
                return true;
            }
        }

        // Heuristic 3: a single content-heavy line that is a short unit repeated many times — catches
        // no-newline char-level ("0000…") and phrase-level ("ありがとう…") loops the line heuristics miss.
        foreach (var line in lines)
        {
            if (line.Length < minLengthForSegmentCheck || !IsMajorityAlphanumeric(line))
            {
                continue;
            }

            var period = SmallestRepeatingPeriod(line);
            if (period > 0 && period <= maxRepeatedSegmentLength && line.Length / period >= minRepeatedSegmentRepeats)
            {
                return true;
            }
        }

        return false;
    }

    // Majority letters/digits → real content. Excludes Markdown table separators ("|---|---|"),
    // horizontal rules ("------"), and dotted leaders ("……") which are punctuation-only and legitimate.
    private static bool IsMajorityAlphanumeric(string line)
    {
        var alnum = 0;
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
            {
                alnum++;
            }
        }

        return alnum * 2 > line.Length;
    }

    // Smallest repeating period via the KMP failure function. For a string that is a unit repeated k
    // times the result is the unit length (e.g. "abab" → 2, "0000" → 1). For non-repetitive text the
    // period approaches the full length, so callers gate on (length / period >= minRepeats).
    private static int SmallestRepeatingPeriod(string s)
    {
        var n = s.Length;
        var fail = new int[n];
        for (var i = 1; i < n; i++)
        {
            var j = fail[i - 1];
            while (j > 0 && s[i] != s[j])
            {
                j = fail[j - 1];
            }

            if (s[i] == s[j])
            {
                j++;
            }

            fail[i] = j;
        }

        return n - fail[n - 1];
    }
}

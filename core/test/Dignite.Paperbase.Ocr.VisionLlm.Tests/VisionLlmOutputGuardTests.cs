using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Ocr.VisionLlm;

public class VisionLlmOutputGuardTests
{
    private const int MaxConsecutive = 24;
    private const double MinDistinctRatio = 0.3;
    private const int MinLines = 40;
    private const int MinSegmentLength = 200;
    private const int MaxSegmentPeriod = 120;
    private const int MinSegmentRepeats = 8;

    private static bool Guard(string markdown, int maxConsecutive = MaxConsecutive)
        => VisionLlmOutputGuard.LooksLikeRepetitionLoop(
            markdown, maxConsecutive, MinDistinctRatio, MinLines,
            MinSegmentLength, MaxSegmentPeriod, MinSegmentRepeats);

    [Fact]
    public void Empty_Or_Short_Output_Is_Not_A_Loop()
    {
        Guard("").ShouldBeFalse();
        Guard("    ").ShouldBeFalse();
        Guard("# Title\n\nHello world.").ShouldBeFalse();
    }

    [Fact]
    public void Consecutive_Repeated_Line_Trips_The_Guard()
    {
        var markdown = string.Join("\n", Enumerable.Repeat("¥980 ポイント", 30));
        Guard(markdown).ShouldBeTrue();
    }

    [Fact]
    public void Interleaved_Low_Distinct_Ratio_Trips_The_Guard()
    {
        // 50 lines, only 5 distinct, never repeated consecutively:
        // heuristic 1 (consecutive run) misses it, heuristic 2 (distinct ratio) catches it.
        var pattern = new[] { "A", "B", "C", "D", "E" };
        var lines = Enumerable.Range(0, 50).Select(i => pattern[i % pattern.Length]);
        Guard(string.Join("\n", lines)).ShouldBeTrue();
    }

    [Fact]
    public void Long_Receipt_With_Many_Distinct_Lines_Does_Not_Trip()
    {
        // A long but legitimate receipt: 60 distinct line items. ratio = 1.0, no consecutive repeats.
        var lines = Enumerable.Range(0, 60).Select(i => $"| Item {i} | {i * 100} JPY |");
        Guard(string.Join("\n", lines)).ShouldBeFalse();
    }

    [Fact]
    public void A_Few_Repeated_Lines_Below_Thresholds_Does_Not_Trip()
    {
        // Real receipts repeat values; a couple of repeats well under the consecutive cap, over a body
        // shorter than MinLines (so the ratio heuristic does not apply), must not be flagged.
        var markdown = "Tea 100\nTea 100\nCoffee 200\nTotal 300";
        Guard(markdown).ShouldBeFalse();
    }

    [Fact]
    public void Single_Line_Char_Flood_Trips_The_Guard()
    {
        // Heuristic 3: a no-newline single-character flood (period 1) the line heuristics miss.
        Guard(new string('0', 300)).ShouldBeTrue();
    }

    [Fact]
    public void Single_Line_Phrase_Loop_Without_Newlines_Trips_The_Guard()
    {
        // Heuristic 3: a no-newline phrase loop (period = phrase length, tiled many times) — the
        // exact #259 death-loop shape the line-based heuristics cannot see.
        var markdown = string.Concat(Enumerable.Repeat("ありがとうございます", 60));
        Guard(markdown).ShouldBeTrue();
    }

    [Fact]
    public void Wide_Markdown_Table_Separator_Is_Not_Flagged()
    {
        // A wide table separator row is a short-period repetition ("| --- " × N) but punctuation-only,
        // so heuristic 3's majority-alphanumeric filter must exclude it (no false positive / data loss).
        var separator = string.Concat(Enumerable.Repeat("| --- ", 40)) + "|"; // ~240 chars
        separator.Length.ShouldBeGreaterThan(MinSegmentLength);
        Guard(separator).ShouldBeFalse();
    }

    [Fact]
    public void Long_Non_Repetitive_Sentence_Is_Not_Flagged()
    {
        // A genuinely varied long line (period ≈ full length → repeats == 1) must pass heuristic 3.
        var sentence =
            "In the quarterly report, revenue increased while costs declined across most regional " +
            "divisions, and the board approved a new strategic initiative focused on sustainable " +
            "growth, operational excellence, and disciplined capital allocation for the coming year.";
        sentence.Length.ShouldBeGreaterThan(MinSegmentLength);
        Guard(sentence).ShouldBeFalse();
    }

    [Fact]
    public void Consecutive_Threshold_Below_Two_Is_Strictest_Not_Disabled()
    {
        // Regression for the inverted-gate bug: configuring the consecutive threshold to 1 (intending the
        // strictest setting) must NOT disable heuristic 1. Three identical lines (below the ratio body
        // size) is caught only if heuristic 1 still runs with the floored threshold of 2.
        VisionLlmOutputGuard.LooksLikeRepetitionLoop(
            "dup\ndup\ndup", maxConsecutiveRepeatedLines: 1,
            MinDistinctRatio, MinLines, MinSegmentLength, MaxSegmentPeriod, MinSegmentRepeats)
            .ShouldBeTrue();
    }
}

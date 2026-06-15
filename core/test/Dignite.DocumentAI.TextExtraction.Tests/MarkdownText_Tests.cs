using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction;

public class MarkdownText_Tests
{
    // --- EscapeInline: neutralizes inline constructs anywhere in the text ---

    [Theory]
    [InlineData("a*b", "a\\*b")]
    [InlineData("_under_", "\\_under\\_")]
    [InlineData("`code`", "\\`code\\`")]
    [InlineData("[link](x)", "\\[link\\](x)")] // [ and ] escaped; ( ) are not inline-significant
    [InlineData("a\\b", "a\\\\b")]             // a pre-existing backslash is escaped too
    [InlineData("<http://x>", "\\<http://x>")] // autolink opener escaped (closing > left as-is)
    [InlineData("a<b", "a\\<b")]
    public void EscapeInline_escapes_inline_specials(string input, string expected)
        => MarkdownText.EscapeInline(input).ShouldBe(expected);

    [Fact]
    public void EscapeInline_leaves_plain_text_and_newlines_untouched()
    {
        MarkdownText.EscapeInline("plain text 3.14 - ok").ShouldBe("plain text 3.14 - ok");
        // Newlines carry soft-break structure that EscapeLineStart inspects later — they must survive.
        MarkdownText.EscapeInline("a\nb").ShouldBe("a\nb");
    }

    // --- EscapeLineStart: escapes a single leading block marker ---

    [Theory]
    [InlineData("# Heading", "\\# Heading")]
    [InlineData("###### six", "\\###### six")]
    [InlineData("> quote", "\\> quote")]
    [InlineData("- bullet", "\\- bullet")]
    [InlineData("+ bullet", "\\+ bullet")]
    [InlineData("* bullet", "\\* bullet")]
    [InlineData("1. ordered", "1\\. ordered")]
    [InlineData("12) ordered", "12\\) ordered")]
    [InlineData("---", "\\---")]
    [InlineData("***", "\\***")]
    [InlineData("___", "\\___")]
    [InlineData("===", "\\===")]              // Setext H1 underline
    [InlineData("--- | ---", "\\--- | ---")]  // GFM table-delimiter row
    [InlineData("| --- |", "\\| --- |")]
    [InlineData(":---:", "\\:---:")]
    [InlineData("-\titem", "\\-\titem")]      // bullet + TAB (not only a space)
    [InlineData("1.\titem", "1\\.\titem")]    // ordered marker + TAB
    public void EscapeLineStart_escapes_a_leading_block_marker(string input, string expected)
        => MarkdownText.EscapeLineStart(input).ShouldBe(expected);

    [Theory]
    [InlineData("Hello world")]
    [InlineData("3.14 is pi")]     // digits + '.' but no following space => not an ordered marker
    [InlineData("12:00 noon")]
    [InlineData("-5 degrees")]     // '-' not followed by a space => not a bullet
    [InlineData("well-known")]     // interior dash
    [InlineData("#hashtag")]       // '#' not followed by whitespace => not a heading
    [InlineData("--")]             // only two => neither a thematic break nor a bullet
    [InlineData("１. 项目")] // full-width digit => not an ASCII ordered marker
    [InlineData("١. item")]          // Arabic-Indic digit => likewise
    [InlineData("# heading")]        // '#' + NBSP => NBSP is not a Markdown space, not a heading
    public void EscapeLineStart_does_not_over_escape_ordinary_text(string input)
        => MarkdownText.EscapeLineStart(input).ShouldBe(input);

    // --- EscapeLineStarts: leading marker per line ---

    [Fact]
    public void EscapeLineStarts_escapes_each_line_independently()
        => MarkdownText.EscapeLineStarts("intro\n- item\nplain").ShouldBe("intro\n\\- item\nplain");

    // --- EscapeBlockText: inline + leading marker on every line ---

    [Fact]
    public void EscapeBlockText_applies_both_inline_and_line_start()
        => MarkdownText.EscapeBlockText("- a*b").ShouldBe("\\- a\\*b");

    [Fact]
    public void EscapeBlockText_neutralizes_a_marker_on_a_soft_break_continuation()
        => MarkdownText.EscapeBlockText("first line\n1. second").ShouldBe("first line\n1\\. second");

    // --- EscapeCell (consolidated from the former MarkdownCell.Escape) ---

    [Fact]
    public void EscapeCell_escapes_pipe_so_a_cell_cannot_split_the_row()
        => MarkdownText.EscapeCell("a|b").ShouldBe("a\\|b");

    [Fact]
    public void EscapeCell_escapes_backslash_before_pipe()
        => MarkdownText.EscapeCell("a\\|b").ShouldBe("a\\\\\\|b");

    [Fact]
    public void EscapeCell_collapses_newlines_to_spaces()
        => MarkdownText.EscapeCell("line1\nline2").ShouldBe("line1 line2");

    [Fact]
    public void EscapeCell_null_becomes_empty() => MarkdownText.EscapeCell(null).ShouldBe(string.Empty);

    // --- EscapeInlineCell (source text in a table cell — #329) ---

    [Fact]
    public void EscapeInlineCell_escapes_inline_metacharacters_and_the_pipe()
        => MarkdownText.EscapeInlineCell("*a*|b").ShouldBe("\\*a\\*\\|b");

    [Fact]
    public void EscapeInlineCell_does_not_double_escape_a_backslash()
        => MarkdownText.EscapeInlineCell("a\\b").ShouldBe("a\\\\b");

    [Fact]
    public void EscapeInlineCell_collapses_newlines_to_spaces()
        => MarkdownText.EscapeInlineCell("line1\nline2").ShouldBe("line1 line2");

    [Fact]
    public void EscapeInlineCell_leaves_an_intraword_underscore_untouched()
        => MarkdownText.EscapeInlineCell("snake_case").ShouldBe("snake_case");

    [Fact]
    public void EscapeInlineCell_null_becomes_empty() => MarkdownText.EscapeInlineCell(null).ShouldBe(string.Empty);

    // --- RenderTable (shared GFM grid renderer — #329) ---

    [Fact]
    public void RenderTable_renders_header_separator_and_rows()
        => MarkdownText.RenderTable(new IReadOnlyList<string>[]
        {
            new[] { "a", "b" },
            new[] { "c", "d" }
        }).ShouldBe("| a | b |\n| --- | --- |\n| c | d |");

    [Fact]
    public void RenderTable_pads_short_rows_to_the_widest_row()
        => MarkdownText.RenderTable(new IReadOnlyList<string>[]
        {
            new[] { "a", "b" },
            new[] { "c" }
        }).ShouldBe("| a | b |\n| --- | --- |\n| c |  |");

    [Fact]
    public void RenderTable_emits_only_header_and_separator_for_a_single_row()
        => MarkdownText.RenderTable(new IReadOnlyList<string>[]
        {
            new[] { "a", "b" }
        }).ShouldBe("| a | b |\n| --- | --- |");

    [Fact]
    public void RenderTable_returns_empty_for_no_rows()
        => MarkdownText.RenderTable(Array.Empty<IReadOnlyList<string>>()).ShouldBe(string.Empty);

    // --- InlineLabel (consolidated from the former MarkdownCell.Inline) ---

    [Fact]
    public void InlineLabel_collapses_all_whitespace_runs()
        => MarkdownText.InlineLabel("a\n  b\tc").ShouldBe("a b c");

    [Fact]
    public void InlineLabel_null_becomes_empty() => MarkdownText.InlineLabel(null).ShouldBe(string.Empty);
}

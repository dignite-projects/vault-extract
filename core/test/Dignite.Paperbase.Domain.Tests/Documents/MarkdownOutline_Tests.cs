using System.Linq;
using Dignite.Paperbase.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

public class MarkdownOutline_Tests
{
    // ── Extract ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_Returns_Empty_For_Null_Or_Empty()
    {
        MarkdownOutline.Extract(null).ShouldBeEmpty();
        MarkdownOutline.Extract(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void Extract_Returns_Empty_When_No_Headers()
    {
        MarkdownOutline.Extract("just a paragraph with no headers at all").ShouldBeEmpty();
    }

    [Fact]
    public void Extract_Captures_Level_Title_And_LineNumber()
    {
        var md = "# Top\n\nbody\n\n## Sub one\n\nbody\n\n### Deeper";
        var headers = MarkdownOutline.Extract(md);

        headers.Count.ShouldBe(3);
        headers[0].Level.ShouldBe(1);
        headers[0].Title.ShouldBe("Top");
        headers[0].LineNumber.ShouldBe(1);
        headers[1].Level.ShouldBe(2);
        headers[1].Title.ShouldBe("Sub one");
        headers[1].LineNumber.ShouldBe(5);
        headers[2].Level.ShouldBe(3);
        headers[2].Title.ShouldBe("Deeper");
        headers[2].LineNumber.ShouldBe(9);
    }

    [Fact]
    public void Extract_Strips_Closing_ATX_Hashes()
    {
        // CommonMark closed-ATX: "## Title ##"
        var headers = MarkdownOutline.Extract("## Title ##");
        headers.Count.ShouldBe(1);
        headers[0].Title.ShouldBe("Title");
    }

    [Fact]
    public void Extract_Ignores_Headers_Inside_Fenced_Code_Blocks()
    {
        // The "## not a header" line is inside a fenced block and must not be picked up.
        var md = "# Real\n\n```\n## not a header\n```\n\n## Real two";
        var headers = MarkdownOutline.Extract(md);

        headers.Count.ShouldBe(2);
        headers[0].Title.ShouldBe("Real");
        headers[1].Title.ShouldBe("Real two");
    }

    [Fact]
    public void Extract_Truncates_At_MaxHeaders()
    {
        var md = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"# h{i}"));
        var headers = MarkdownOutline.Extract(md, maxHeaders: 3);
        headers.Count.ShouldBe(3);
        headers[2].Title.ShouldBe("h3");
    }

    [Fact]
    public void Extract_Allows_Up_To_Three_Leading_Spaces_But_Not_Tabs()
    {
        // 0-3 leading spaces is a valid ATX header per CommonMark; >3 is a code block.
        MarkdownOutline.Extract("   # Indented").Count.ShouldBe(1);
        MarkdownOutline.Extract("    # Too indented").ShouldBeEmpty();
    }

    [Fact]
    public void Extract_Throws_On_NonPositive_MaxHeaders()
    {
        Should.Throw<System.ArgumentOutOfRangeException>(
            () => MarkdownOutline.Extract("# x", maxHeaders: 0));
    }

    // ── Grep ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Grep_Returns_Empty_For_Empty_Inputs()
    {
        MarkdownOutline.Grep(null, "foo").ShouldBeEmpty();
        MarkdownOutline.Grep(string.Empty, "foo").ShouldBeEmpty();
        MarkdownOutline.Grep("some text", string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void Grep_Is_Case_Insensitive()
    {
        var md = "Header\nLine with FOO inside\nfooter";
        var matches = MarkdownOutline.Grep(md, "foo", contextLines: 0);
        matches.Count.ShouldBe(2);   // "FOO" line + "footer" line both contain "foo"
    }

    [Fact]
    public void Grep_Includes_Surrounding_Context_Lines()
    {
        var md = "line0\nline1\nNEEDLE\nline3\nline4";
        var matches = MarkdownOutline.Grep(md, "needle", contextLines: 1);
        matches.Count.ShouldBe(1);
        matches[0].ShouldBe("line1\nNEEDLE\nline3");
    }

    [Fact]
    public void Grep_Merges_Overlapping_Windows_Without_Duplicating_Lines()
    {
        // Two hits 2 lines apart with contextLines=2 → windows overlap → merged.
        var md = "a\nNEEDLE\nb\nNEEDLE\nc";
        var matches = MarkdownOutline.Grep(md, "needle", contextLines: 2);
        matches.Count.ShouldBe(1);   // merged into one snippet
        matches[0].ShouldContain("NEEDLE\nb\nNEEDLE");
        // No duplicated NEEDLE lines.
        var occurrences = matches[0].Split("NEEDLE").Length - 1;
        occurrences.ShouldBe(2);
    }

    [Fact]
    public void Grep_Caps_At_MaxMatches()
    {
        // Generate 5 non-overlapping NEEDLE hits, ask for 2.
        var lines = new System.Collections.Generic.List<string>();
        for (var i = 0; i < 5; i++)
        {
            lines.Add("padding A");
            lines.Add("padding B");
            lines.Add("padding C");
            lines.Add("NEEDLE-" + i);
            lines.Add("padding D");
            lines.Add("padding E");
            lines.Add("padding F");
        }
        var md = string.Join('\n', lines);

        var matches = MarkdownOutline.Grep(md, "needle", contextLines: 0, maxMatches: 2);
        matches.Count.ShouldBe(2);
        matches[0].ShouldContain("NEEDLE-0");
        matches[1].ShouldContain("NEEDLE-1");
    }

    [Fact]
    public void Grep_Throws_On_Negative_ContextLines_Or_NonPositive_MaxMatches()
    {
        Should.Throw<System.ArgumentOutOfRangeException>(
            () => MarkdownOutline.Grep("text", "q", contextLines: -1));
        Should.Throw<System.ArgumentOutOfRangeException>(
            () => MarkdownOutline.Grep("text", "q", maxMatches: 0));
    }
}

using Dignite.Vault.Extract.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

public class MarkdownTitleExtractor_Tests
{
    [Fact]
    public void Returns_Null_For_Null_Or_Empty()
    {
        MarkdownTitleExtractor.ExtractTitle(null).ShouldBeNull();
        MarkdownTitleExtractor.ExtractTitle(string.Empty).ShouldBeNull();
        MarkdownTitleExtractor.ExtractTitle("   \n\n  ").ShouldBeNull();
    }

    [Fact]
    public void Picks_First_H1_Over_Later_Headings()
    {
        var md = "# First Title\n\n## Second\n\nbody";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("First Title");
    }

    [Fact]
    public void Falls_Back_To_First_H2_When_No_H1()
    {
        var md = "## Section A\n\nbody";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("Section A");
    }

    [Fact]
    public void Strips_Inline_Markdown_From_Heading()
    {
        var md = "# **Bold** title with `code` and [link](http://x)";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("Bold title with code and link");
    }

    [Fact]
    public void Strips_Trailing_Hashes_From_Heading()
    {
        // closed-ATX heading: # title #
        MarkdownTitleExtractor.ExtractTitle("# Title ##").ShouldBe("Title");
    }

    [Fact]
    public void Falls_Back_To_First_Paragraph_When_No_Heading()
    {
        var md = "First paragraph line.\n\nSecond.";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("First paragraph line.");
    }

    [Fact]
    public void Skips_Code_Fences_When_Falling_Back_To_Paragraph()
    {
        var md = "```\nshould be skipped\n```\n\nReal first paragraph.";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("Real first paragraph.");
    }

    [Fact]
    public void Skips_Table_Separators_And_Hr_When_Falling_Back()
    {
        var md = "|---|---|\n---\n***\nFirst real text.";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBe("First real text.");
    }

    [Fact]
    public void Strips_List_Markers_When_Falling_Back()
    {
        MarkdownTitleExtractor.ExtractTitle("- item one\n- item two").ShouldBe("item one");
        MarkdownTitleExtractor.ExtractTitle("1. ordered item").ShouldBe("ordered item");
    }

    [Fact]
    public void Truncates_Long_Title_To_MaxLength()
    {
        var longText = new string('x', 500);
        var title = MarkdownTitleExtractor.ExtractTitle("# " + longText);
        title!.Length.ShouldBe(DocumentConsts.MaxTitleLength);
    }

    [Fact]
    public void Honors_Custom_MaxLength()
    {
        MarkdownTitleExtractor.ExtractTitle("# Hello World", maxLength: 5).ShouldBe("Hello");
    }

    /// <summary>
    /// #491: the cut must not split a surrogate pair. This is not academic — `Document.SetTitle` can only repair a lone
    /// surrogate on its `> MaxTitleLength` branch, and a title pre-cut to exactly the limit never enters it, so a split
    /// pair here would be persisted and served to MCP clients.
    /// </summary>
    [Fact]
    public void Truncation_Never_Splits_A_Surrogate_Pair()
    {
        // maxLength=2 lands inside the pair, so the high surrogate is dropped and only "A" survives.
        var title = MarkdownTitleExtractor.ExtractTitle("# A\U0001F600B", maxLength: 2);

        title.ShouldBe("A");
        char.IsHighSurrogate(title![^1]).ShouldBeFalse();
    }

    [Fact]
    public void Collapses_Internal_Whitespace()
    {
        MarkdownTitleExtractor.ExtractTitle("#   spaced     out\ttitle  ")
            .ShouldBe("spaced out title");
    }

    [Fact]
    public void Returns_Null_When_Document_Has_Only_Empty_Code_Block()
    {
        var md = "```\n```";
        MarkdownTitleExtractor.ExtractTitle(md).ShouldBeNull();
    }
}

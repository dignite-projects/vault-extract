using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

public class MarkdownCell_Tests
{
    [Fact]
    public void Escapes_pipe_so_a_cell_cannot_split_the_row()
    {
        MarkdownCell.Escape("a|b").ShouldBe("a\\|b");
    }

    [Fact]
    public void Escapes_backslash_before_pipe_so_a_trailing_backslash_does_not_neutralize_the_escape()
    {
        // "a\|b": the backslash is escaped first (-> "a\\"), then the pipe (-> "\\|"), giving "a\\\\|b"
        // which renders as a literal backslash followed by an escaped pipe — the cell stays one column.
        MarkdownCell.Escape("a\\|b").ShouldBe("a\\\\\\|b");
    }

    [Fact]
    public void Collapses_newlines_to_spaces()
    {
        MarkdownCell.Escape("line1\nline2").ShouldBe("line1 line2");
    }

    [Fact]
    public void Null_becomes_empty()
    {
        MarkdownCell.Escape(null).ShouldBe(string.Empty);
    }
}

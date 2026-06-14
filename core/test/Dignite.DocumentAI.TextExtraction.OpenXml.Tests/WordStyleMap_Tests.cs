using Shouldly;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Unit tests for <see cref="WordStyleMap.HeadingLevel"/> — the structural backbone of the DOCX rebuild.
/// These cover in-scope production branches that the DocxFixtures package cannot reach via <c>w:pStyle</c>
/// alone (the <c>w:outlineLvl</c> fallback) plus the clamp and the linked-character-style guard, by calling
/// the internal static method directly (the same approach <c>DocxIncompleteReason_Tests</c> uses).
/// </summary>
public class WordStyleMap_Tests
{
    private static W.Paragraph WithStyle(string styleId)
        => new(new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }));

    private static W.Paragraph WithOutline(int level)
        => new(new W.ParagraphProperties(new W.OutlineLevel { Val = level }));

    [Fact]
    public void Maps_heading_style_ids_to_levels()
    {
        WordStyleMap.HeadingLevel(WithStyle("Heading1")).ShouldBe(1);
        WordStyleMap.HeadingLevel(WithStyle("Heading2")).ShouldBe(2);
        WordStyleMap.HeadingLevel(WithStyle("heading3")).ShouldBe(3); // case-insensitive
    }

    [Fact]
    public void Maps_title_style_to_top_level()
    {
        WordStyleMap.HeadingLevel(WithStyle("Title")).ShouldBe(1);
    }

    [Fact]
    public void Clamps_heading_levels_above_six_to_six()
    {
        // Word allows nine heading levels; ATX Markdown allows six.
        WordStyleMap.HeadingLevel(WithStyle("Heading7")).ShouldBe(6);
        WordStyleMap.HeadingLevel(WithStyle("Heading9")).ShouldBe(6);
    }

    [Fact]
    public void Does_not_treat_a_linked_character_style_as_a_heading()
    {
        // "Heading1Char" is the auto-generated linked character style, not a paragraph heading.
        WordStyleMap.HeadingLevel(WithStyle("Heading1Char")).ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_outline_level_when_there_is_no_heading_style()
    {
        WordStyleMap.HeadingLevel(WithOutline(0)).ShouldBe(1);  // 0-based: level 0 -> H1
        WordStyleMap.HeadingLevel(WithOutline(2)).ShouldBe(3);
        WordStyleMap.HeadingLevel(WithOutline(8)).ShouldBe(6);  // 8 + 1 = 9, clamped to 6
    }

    [Fact]
    public void Outline_level_nine_is_body_text_not_a_heading()
    {
        WordStyleMap.HeadingLevel(WithOutline(9)).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_a_plain_paragraph()
    {
        WordStyleMap.HeadingLevel(new W.Paragraph()).ShouldBeNull();
        WordStyleMap.HeadingLevel(WithStyle("Normal")).ShouldBeNull();
    }
}

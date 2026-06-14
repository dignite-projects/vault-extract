using Shouldly;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Unit tests for the numbering-resolution branches of <see cref="WordListNumbering.Resolve"/> that the
/// fixture-based integration tests cannot reach without a numbering part — the "not a list", the
/// remove-numbering sentinel, and the unresolved-definition bullet fallback. The bullet / ordered / nested
/// happy paths are covered by the fixture-driven <c>DocxExtractor_Tests</c>.
/// </summary>
public class WordListNumbering_Tests
{
    private static W.Paragraph WithNumbering(int numId, int level)
        => new(new W.ParagraphProperties(
            new W.NumberingProperties(
                new W.NumberingLevelReference { Val = level },
                new W.NumberingId { Val = numId })));

    [Fact]
    public void Returns_null_for_a_paragraph_without_numbering()
    {
        WordListNumbering.Resolve(new W.Paragraph(), numberingPart: null).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_the_zero_numId_remove_numbering_sentinel()
    {
        // w:numId val="0" is Word's "this paragraph has numbering removed" marker, not a real list item.
        WordListNumbering.Resolve(WithNumbering(0, 0), numberingPart: null).ShouldBeNull();
    }

    [Fact]
    public void Defaults_to_a_bullet_when_the_numbering_definition_cannot_be_resolved()
    {
        // numId present but no numbering part to resolve the format -> still a list item (it carries
        // w:numPr), defaulting to a bullet at the referenced level rather than being dropped.
        var info = WordListNumbering.Resolve(WithNumbering(3, 2), numberingPart: null);

        info.ShouldNotBeNull();
        info!.Value.Level.ShouldBe(2);
        info.Value.Ordered.ShouldBeFalse();
    }
}

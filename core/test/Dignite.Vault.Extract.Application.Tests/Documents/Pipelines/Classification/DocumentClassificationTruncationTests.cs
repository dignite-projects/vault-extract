using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Regression protection: <see cref="DocumentClassificationWorkflow.TruncateAtCharBoundary"/> must not split surrogate pairs.
///
/// Background: classification truncates the document prefix by MaxTextLengthPerExtraction UTF-16 code units
/// before sending it to the LLM. A naive <c>markdown[..N]</c> leaves half a code point when N lands inside a
/// surrogate pair; UTF-8 encoding then degrades it to U+FFFD for the LLM.
/// The cut point is at the discarded tail, so if the final kept code unit is a high surrogate, dropping it too
/// is harmless.
/// </summary>
public class DocumentClassificationTruncationTests
{
    [Fact]
    public void Returns_Text_Unchanged_When_Within_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("hello", 10).ShouldBe("hello");
        DocumentClassificationWorkflow.TruncateAtCharBoundary("hello", 5).ShouldBe("hello");
    }

    [Fact]
    public void Truncates_Plain_Text_At_Char_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("abcdef", 3).ShouldBe("abc");
    }

    [Fact]
    public void Does_Not_Split_A_Surrogate_Pair_At_The_Cut()
    {
        // "A" + U+1F600 (one surrogate pair = two UTF-16 code units) + "B".
        // Cutting at maxChars=2 lands inside the surrogate pair; it should drop the high surrogate, return "A"
        // with length 1, and leave no half code point.
        var text = "A\U0001F600B";
        var result = DocumentClassificationWorkflow.TruncateAtCharBoundary(text, 2);

        result.ShouldBe("A");
        result.Length.ShouldBe(1);
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
    }

    [Fact]
    public void Keeps_A_Whole_Surrogate_Pair_When_It_Fits()
    {
        // maxChars=3 contains "A" + the whole surrogate pair, so it is kept as-is with length 3.
        var text = "A\U0001F600B";
        var result = DocumentClassificationWorkflow.TruncateAtCharBoundary(text, 3);

        result.ShouldBe("A\U0001F600");
        result.Length.ShouldBe(3);
    }

    [Fact]
    public void Returns_Empty_For_Non_Positive_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("abc", 0).ShouldBe(string.Empty);
    }
}

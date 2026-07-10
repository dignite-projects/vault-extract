using Dignite.Vault.Extract.Ai;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #491: every truncation on an LLM-facing path goes through <see cref="TextTruncator.AtCharBoundary"/>. The property
/// that matters is that a cut never emits a lone surrogate — a raw <c>text[..n]</c> range slice can, and what a
/// provider or serializer does with one is not something the channel layer should be discovering in production.
/// </summary>
public class TextTruncatorTests
{
    [Fact]
    public void Returns_The_Input_When_It_Already_Fits()
    {
        TextTruncator.AtCharBoundary("hello", 10).ShouldBe("hello");
        TextTruncator.AtCharBoundary("hello", 5).ShouldBe("hello");
    }

    [Fact]
    public void Truncates_Plain_Text_At_The_Char_Limit()
    {
        TextTruncator.AtCharBoundary("abcdef", 3).ShouldBe("abc");
    }

    [Fact]
    public void Non_Positive_Limit_Yields_Empty()
    {
        TextTruncator.AtCharBoundary("abc", 0).ShouldBeEmpty();
        TextTruncator.AtCharBoundary("abc", -1).ShouldBeEmpty();
    }

    [Fact]
    public void Never_Splits_A_Surrogate_Pair()
    {
        // "A" + U+1F600 (a surrogate pair): total Length 3. Cutting at 2 lands inside the pair, so the high
        // surrogate is dropped and "A" is returned rather than a lone, unpaired high surrogate.
        var text = "A😀";
        text.Length.ShouldBe(3);

        var result = TextTruncator.AtCharBoundary(text, 2);

        result.ShouldBe("A");
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
    }

    [Fact]
    public void Keeps_A_Whole_Surrogate_Pair_That_Fits()
    {
        var text = "A😀";

        TextTruncator.AtCharBoundary(text, 3).ShouldBe(text);
    }
}

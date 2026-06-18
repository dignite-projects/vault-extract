using Dignite.DocumentAI.Abstractions.TextExtraction;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.Ai;

/// <summary>
/// Unit tests for <see cref="ImageOcrMarkup"/> (#371): the in-band <c>[Image OCR]…[End OCR]</c> sentinels that bracket
/// an embedded-figure OCR transcription in the working (marked) Markdown. The two load-bearing invariants are that
/// <see cref="ImageOcrMarkup.Strip"/> is the exact inverse of <see cref="ImageOcrMarkup.Wrap"/> (so a stripped
/// document equals the pre-#371 inline output byte for byte) and that only WHOLE trimmed-line sentinels are stripped
/// (so ordinary prose that merely mentions the label survives). Pure unit test; no ABP host required.
/// </summary>
public class ImageOcrMarkup_Tests
{
    [Fact]
    public void Strip_Is_The_Inverse_Of_Wrap()
    {
        ImageOcrMarkup.Strip(ImageOcrMarkup.Wrap("inv", 3)).ShouldBe("inv");
    }

    [Fact]
    public void Strip_Removes_Sentinel_Lines_Leaving_Surrounding_Content_In_Place()
    {
        var marked = "a\n\n" + ImageOcrMarkup.Wrap("inv", 3) + "\n\nb";
        ImageOcrMarkup.Strip(marked).ShouldBe("a\n\ninv\n\nb");
    }

    [Fact]
    public void Strip_Returns_Text_With_No_Sentinels_Unchanged()
    {
        const string plain = "just some prose\nover two lines";
        ImageOcrMarkup.Strip(plain).ShouldBe(plain);
    }

    [Fact]
    public void Contains_Is_True_After_Wrap_And_False_On_Plain_Text()
    {
        ImageOcrMarkup.Contains(ImageOcrMarkup.Wrap("inv", 3)).ShouldBeTrue();
        ImageOcrMarkup.Contains("just some prose").ShouldBeFalse();
    }

    [Fact]
    public void TryParsePage_And_Line_Predicates_Recognize_Both_Open_Forms_And_The_Close()
    {
        ImageOcrMarkup.TryParsePage("[Image OCR p:3]").ShouldBe(3);
        ImageOcrMarkup.TryParsePage("[Image OCR]").ShouldBeNull();

        ImageOcrMarkup.IsOpenLine("[Image OCR p:3]").ShouldBeTrue();
        ImageOcrMarkup.IsOpenLine("[Image OCR]").ShouldBeTrue();

        ImageOcrMarkup.IsCloseLine("[End OCR]").ShouldBeTrue();
    }

    [Fact]
    public void Strip_Leaves_A_Body_Line_That_Merely_Mentions_The_Label_As_A_Substring()
    {
        // Only a WHOLE trimmed-line sentinel is removed; the label embedded mid-line is ordinary prose and survives.
        ImageOcrMarkup.Strip("see [Image OCR] note").ShouldBe("see [Image OCR] note");
    }
}

using Dignite.Vault.Extract.Abstractions.Parse;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Unit tests for <see cref="ImageOcrMarkup"/> (#371/#381): the in-band <c>*[Image OCR]*…*[End OCR]*</c> provenance
/// markers that bracket an embedded-figure OCR transcription in the document Markdown. Since #381 the markers are
/// MarkItDown-aligned (italic-wrapped, salt-free) and stay in <c>Document.Markdown</c> at the egress — they are no
/// longer stripped before persistence. <see cref="ImageOcrMarkup.Strip"/> survives as a content utility for deriving
/// a spawned sub-document's seed and remains the exact inverse of <see cref="ImageOcrMarkup.Wrap"/>; only WHOLE
/// trimmed-line markers are matched (so ordinary prose that merely mentions the label survives). Pure unit test; no
/// ABP host required.
/// </summary>
public class ImageOcrMarkup_Tests
{
    [Fact]
    public void Markers_Are_MarkItDown_Aligned_And_Salt_Free()
    {
        // #381: the markers are byte-identical to MarkItDown's own — italic-wrapped, carrying no salt — and the page
        // anchor is a *[Image OCR p:N]* suffix form. Pin the exact strings so the egress format cannot drift silently.
        ImageOcrMarkup.OpenMarker.ShouldBe("*[Image OCR]*");
        ImageOcrMarkup.CloseMarker.ShouldBe("*[End OCR]*");
        ImageOcrMarkup.Wrap("inv", pageNumber: 3).ShouldBe("*[Image OCR p:3]*\ninv\n*[End OCR]*");
        ImageOcrMarkup.Wrap("inv", pageNumber: null).ShouldBe("*[Image OCR]*\ninv\n*[End OCR]*");
    }

    [Fact]
    public void Strip_Is_The_Inverse_Of_Wrap()
    {
        ImageOcrMarkup.Strip(ImageOcrMarkup.Wrap("inv", 3)).ShouldBe("inv");
    }

    [Fact]
    public void Strip_Removes_Marker_Lines_Leaving_Surrounding_Content_In_Place()
    {
        var marked = "a\n\n" + ImageOcrMarkup.Wrap("inv", 3) + "\n\nb";
        ImageOcrMarkup.Strip(marked).ShouldBe("a\n\ninv\n\nb");
    }

    [Fact]
    public void Strip_Returns_Text_With_No_Markers_Unchanged()
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
        ImageOcrMarkup.TryParsePage("*[Image OCR p:3]*").ShouldBe(3);
        ImageOcrMarkup.TryParsePage(ImageOcrMarkup.OpenMarker).ShouldBeNull();

        ImageOcrMarkup.IsOpenLine("*[Image OCR p:3]*").ShouldBeTrue();
        ImageOcrMarkup.IsOpenLine(ImageOcrMarkup.OpenMarker).ShouldBeTrue();

        ImageOcrMarkup.IsCloseLine(ImageOcrMarkup.CloseMarker).ShouldBeTrue();
    }

    [Fact]
    public void Strip_Leaves_A_Body_Line_That_Merely_Mentions_The_Label_As_A_Substring()
    {
        // Only a WHOLE trimmed-line marker is removed; the label embedded mid-line is ordinary prose and survives.
        var midLine = "see " + ImageOcrMarkup.OpenMarker + " note";
        ImageOcrMarkup.Strip(midLine).ShouldBe(midLine);
    }

    [Fact]
    public void Reingested_MarkItDown_Markers_Are_Recognized_As_Provenance_Markers()
    {
        // #381: with the salt removed, our markers ARE MarkItDown's own bare *[Image OCR]* / *[End OCR]*. A document
        // MarkItDown produced and then re-ingested carries those exact lines as provenance-annotated content — they
        // are recognized as a figure span (intended, no special-casing) and, since the pipeline no longer strips,
        // they stay in the egress Markdown rather than being silently deleted.
        ImageOcrMarkup.IsOpenLine("*[Image OCR]*").ShouldBeTrue();
        ImageOcrMarkup.IsOpenLine("*[Image OCR p:3]*").ShouldBeTrue();
        ImageOcrMarkup.IsCloseLine("*[End OCR]*").ShouldBeTrue();

        // A non-italic "[Image OCR]" line (missing the * wrapping) is NOT our marker — whole-line exact match.
        ImageOcrMarkup.IsOpenLine("[Image OCR]").ShouldBeFalse();

        const string reingested = "intro\n*[Image OCR]*\nold transcription\n*[End OCR]*\noutro";
        ImageOcrMarkup.Contains(reingested).ShouldBeTrue();
        ImageOcrMarkup.ExtractBodies(reingested).ShouldBe("old transcription");
    }
}

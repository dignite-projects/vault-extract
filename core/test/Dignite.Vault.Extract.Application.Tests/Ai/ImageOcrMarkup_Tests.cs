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

    [Fact]
    public void Wrap_With_Image_Reference_Inlines_It_As_The_First_Body_Line_Inside_The_Span()
    {
        // #477: the figures/{hash} reference travels INSIDE the span (first body line, right after the open marker),
        // so the sub-document segmentation pass can correlate a figure to its blob (#478). The bare marker is unchanged.
        ImageOcrMarkup.Wrap("inv", pageNumber: 3, imageReference: "figures/abc.png")
            .ShouldBe("*[Image OCR p:3]*\n![figure](figures/abc.png)\ninv\n*[End OCR]*");
    }

    [Fact]
    public void Wrap_Without_Image_Reference_Is_Byte_Identical_To_Before()
    {
        // Retention off (null / empty reference) leaves the span exactly as before #477 — the regression guard.
        ImageOcrMarkup.Wrap("inv", 3, imageReference: null).ShouldBe("*[Image OCR p:3]*\ninv\n*[End OCR]*");
        ImageOcrMarkup.Wrap("inv", 3, imageReference: "").ShouldBe("*[Image OCR p:3]*\ninv\n*[End OCR]*");
    }

    [Fact]
    public void IsImageReferenceLine_Recognizes_The_Figure_Reference_By_Its_Target()
    {
        ImageOcrMarkup.IsImageReferenceLine("![figure](figures/abc.png)").ShouldBeTrue();
        ImageOcrMarkup.IsImageReferenceLine("  ![figure](figures/abc.png)  ").ShouldBeTrue(); // trimmed
        // Recognition keys on the `](figures/` document-relative target, so it survives an alt-text change.
        ImageOcrMarkup.IsImageReferenceLine("![diagram](figures/xyz.jpg)").ShouldBeTrue();
        // An ordinary image that is NOT a retained-figure reference is untouched.
        ImageOcrMarkup.IsImageReferenceLine("![logo](https://example.com/a.png)").ShouldBeFalse();
        ImageOcrMarkup.IsImageReferenceLine("just prose").ShouldBeFalse();
    }

    [Fact]
    public void Strip_And_ExtractBodies_Drop_The_Retained_Figure_Reference_Line()
    {
        // #477: the spawned child sub-document's seed is the transcription only (#373) — the retained image belongs to
        // the container. Strip / ExtractBodies drop the figures/{hash} reference; it survives only in the container egress.
        var marked = ImageOcrMarkup.Wrap("inv total 100", pageNumber: 3, imageReference: "figures/abc.png");
        ImageOcrMarkup.Strip(marked).ShouldBe("inv total 100");
        ImageOcrMarkup.ExtractBodies(marked).ShouldBe("inv total 100");
    }
}

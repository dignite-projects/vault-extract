using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Unit tests for <see cref="DefaultTextExtractor"/> dispatch-by-extension: image extensions go straight to
/// OCR, everything else goes to the matching Markdown provider, and a PDF with no usable text layer falls
/// back to whole-page OCR. The OCR provider and Markdown providers are NSubstitute stubs, so each branch is
/// driven precisely (e.g. an "empty Markdown" PDF) without depending on any real provider's behavior.
/// </summary>
public class DefaultTextExtractorDispatch_Tests
{
    private static TextExtractionContext Context(string ext, string contentType = "application/octet-stream", IList<string>? languageHints = null)
        => new()
        {
            FileExtension = ext,
            ContentType = contentType,
            LanguageHints = languageHints ?? new List<string>()
        };

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".tiff")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    [InlineData(".JPG")]  // case-insensitive
    [InlineData(".PNG")]
    public async Task Should_Route_Image_Extensions_Straight_To_Ocr(string extension)
    {
        var ocr = TestDoubles.OcrReturning(new OcrResult { Markdown = "image ocr", ProviderName = "FakeOcr" });
        var markdown = TestDoubles.MarkdownProvider(MarkdownProviderPriorities.Fallback, handles: extension);
        var sut = TestDoubles.Extractor(ocr, new[] { markdown });

        var result = await sut.ExtractAsync(TestDoubles.Bytes(0xFF, 0xD8), Context(extension, "image/jpeg"));

        result.UsedOcr.ShouldBeTrue();
        result.Markdown.ShouldBe("image ocr");
        await ocr.Received(1).RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        // The image path returns before any Markdown provider is considered.
        await markdown.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Route_Non_Image_Extension_To_Markdown_Provider_Without_Ocr()
    {
        var ocr = TestDoubles.OcrReturning();
        var markdown = TestDoubles.MarkdownProvider(
            MarkdownProviderPriorities.Specialized,
            handles: ".txt",
            new TextExtractionResult { Markdown = "digital text", ProviderName = "FakeMarkdown" });
        var sut = TestDoubles.Extractor(ocr, new[] { markdown });

        var result = await sut.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")), Context(".txt", "text/plain"));

        result.UsedOcr.ShouldBeFalse();
        result.Markdown.ShouldBe("digital text");
        await ocr.DidNotReceive().RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Fall_Back_To_Ocr_When_Pdf_Has_No_Usable_Text_Layer()
    {
        // A PdfPig-style provider returns empty for a scanned/image-only PDF; the orchestrator owns the
        // single whole-page OCR fallback.
        var ocr = TestDoubles.OcrReturning(new OcrResult { Markdown = "ocr of scanned pdf", ProviderName = "FakeOcr" });
        var pdf = TestDoubles.MarkdownProvider(
            MarkdownProviderPriorities.Specialized,
            handles: ".pdf",
            new TextExtractionResult { Markdown = string.Empty });
        var sut = TestDoubles.Extractor(ocr, new[] { pdf });

        var result = await sut.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7")), Context(".pdf", "application/pdf"));

        result.UsedOcr.ShouldBeTrue();
        result.Markdown.ShouldBe("ocr of scanned pdf");
        await ocr.Received(1).RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Not_Fall_Back_To_Ocr_When_Pdf_Has_A_Text_Layer()
    {
        var ocr = TestDoubles.OcrReturning();
        var pdf = TestDoubles.MarkdownProvider(
            MarkdownProviderPriorities.Specialized,
            handles: ".pdf",
            new TextExtractionResult { Markdown = "# Real digital PDF text", ProviderName = "FakePdf" });
        var sut = TestDoubles.Extractor(ocr, new[] { pdf });

        var result = await sut.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7")), Context(".pdf", "application/pdf"));

        result.UsedOcr.ShouldBeFalse();
        result.Markdown.ShouldBe("# Real digital PDF text");
        await ocr.DidNotReceive().RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("   \n\n  ")]      // whitespace only
    [InlineData("--- \n\n ## ")]   // punctuation / markup only, no letter or digit
    public async Task Should_Treat_Pdf_Markdown_Without_Letters_Or_Digits_As_No_Text_Layer(string emptyish)
    {
        var ocr = TestDoubles.OcrReturning(new OcrResult { Markdown = "ocr fallback", ProviderName = "FakeOcr" });
        var pdf = TestDoubles.MarkdownProvider(
            MarkdownProviderPriorities.Specialized,
            handles: ".pdf",
            new TextExtractionResult { Markdown = emptyish });
        var sut = TestDoubles.Extractor(ocr, new[] { pdf });

        var result = await sut.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("%PDF")), Context(".pdf", "application/pdf"));

        result.UsedOcr.ShouldBeTrue();
        result.Markdown.ShouldBe("ocr fallback");
    }

    [Fact]
    public async Task Should_Not_Fall_Back_To_Ocr_For_A_Non_Pdf_With_Empty_Markdown()
    {
        // The OCR fallback is PDF-only: an empty Markdown result for any other extension is passed through
        // untouched (no OCR retry).
        var ocr = TestDoubles.OcrReturning();
        var txt = TestDoubles.MarkdownProvider(
            MarkdownProviderPriorities.Fallback,
            handles: ".txt",
            new TextExtractionResult { Markdown = string.Empty });
        var sut = TestDoubles.Extractor(ocr, new[] { txt });

        var result = await sut.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("")), Context(".txt", "text/plain"));

        result.UsedOcr.ShouldBeFalse();
        result.Markdown.ShouldBe(string.Empty);
        await ocr.DidNotReceive().RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Pass_Context_Language_Hints_To_Ocr_When_Provided()
    {
        var ocr = TestDoubles.OcrReturning();
        var sut = TestDoubles.Extractor(ocr, new[] { TestDoubles.MarkdownProvider(0, ".x") });

        await sut.ExtractAsync(
            TestDoubles.Bytes(0xFF, 0xD8),
            Context(".jpg", "image/jpeg", languageHints: new List<string> { "fr", "de" }));

        await ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.SequenceEqual(new[] { "fr", "de" }) && o.ContentType == "image/jpeg"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Fall_Back_To_Default_Language_Hints_When_Context_Has_None()
    {
        var ocr = TestDoubles.OcrReturning();
        var options = new ExtractOcrOptions { DefaultLanguageHints = new List<string> { "it", "es" } };
        var sut = TestDoubles.Extractor(ocr, new[] { TestDoubles.MarkdownProvider(0, ".x") }, options);

        await sut.ExtractAsync(TestDoubles.Bytes(0xFF, 0xD8), Context(".jpg", "image/jpeg"));

        await ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.SequenceEqual(new[] { "it", "es" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Propagate_Ocr_Result_Fields_On_The_Ocr_Path()
    {
        var ocr = TestDoubles.OcrReturning(new OcrResult
        {
            Markdown = "ocr md",
            DetectedLanguage = "ja",
            ProviderName = "FakeOcr",
            IsComplete = false,
            IncompleteReason = "truncated"
        });
        var sut = TestDoubles.Extractor(ocr, new[] { TestDoubles.MarkdownProvider(0, ".x") });

        var result = await sut.ExtractAsync(TestDoubles.Bytes(0xFF, 0xD8), Context(".png", "image/png"));

        result.UsedOcr.ShouldBeTrue();
        result.DetectedLanguage.ShouldBe("ja");
        result.ProviderName.ShouldBe("FakeOcr");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldBe("truncated");
    }
}

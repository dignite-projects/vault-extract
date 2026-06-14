using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

public class DocxExtractor_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private DocxExtractor CreateExtractor(
        int minImagePixels = 0,
        int maxImages = 50,
        long maxImageBytes = 16L * 1024 * 1024,
        DocumentAIOcrOptions? ocrOptions = null)
        => new(
            _ocr,
            Options.Create(new OpenXmlExtractorOptions
            {
                MinImagePixels = minImagePixels,
                MaxImagesPerFile = maxImages,
                MaxImageBytesPerImage = maxImageBytes
            }),
            // Default to empty hints so unrelated tests don't get defaults injected unexpectedly.
            Options.Create(ocrOptions ?? new DocumentAIOcrOptions { DefaultLanguageHints = new List<string>() }));

    private static TextExtractionContext DocxContext()
        => new() { ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileExtension = ".docx" };

    private void StubOcr(string markdown, bool isComplete = true, string? reason = null)
        => _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new OcrResult
            {
                Markdown = markdown,
                ProviderName = "FakeOcr",
                IsComplete = isComplete,
                IncompleteReason = reason
            });

    private static DocxFixtures.ImageSpec Png(string? alt, long extent = 914400)
        => new(TinyPng.CreateSolid(48, 48), "image/png", alt, extent, extent);

    [Fact]
    public async Task Extracts_paragraph_text()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("The quick brown fox."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("The quick brown fox.");
        result.UsedOcr.ShouldBeFalse();
        result.ProviderName.ShouldBe(DocxExtractor.ProviderIdentifier);
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Renders_heading_styles_as_atx_headings()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Heading("Annual Report", 1)
            .Heading("Financials", 2)
            .Paragraph("Body text."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# Annual Report");
        result.Markdown.ShouldContain("## Financials");
        result.Markdown.ShouldContain("Body text.");
    }

    [Fact]
    public async Task Maps_title_style_to_top_heading_level()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .StyledParagraph("Document Title", "Title"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# Document Title");
    }

    [Fact]
    public async Task Collapses_a_multi_line_heading_to_one_line()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Heading("Annual Report\n2024", 1));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# Annual Report 2024");
        result.Markdown.ShouldNotContain("Annual Report  2024");
    }

    [Fact]
    public async Task Inlines_image_transcription_at_its_reading_position()
    {
        StubOcr("BRAVO");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("ALPHA")
            .Image(Png(alt: null))
            .Paragraph("CHARLIE"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        var alpha = result.Markdown.IndexOf("ALPHA", StringComparison.Ordinal);
        var bravo = result.Markdown.IndexOf("BRAVO", StringComparison.Ordinal);
        var charlie = result.Markdown.IndexOf("CHARLIE", StringComparison.Ordinal);

        alpha.ShouldBeGreaterThanOrEqualTo(0);
        bravo.ShouldBeGreaterThan(alpha, "the figure transcription must come after the text above it");
        charlie.ShouldBeGreaterThan(bravo, "the figure transcription must come before the text below it");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Feeds_image_bytes_to_the_ocr_provider()
    {
        StubOcr("FIGURE");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null)));

        await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.ContentType.StartsWith("image/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uses_native_alt_text_as_caption()
    {
        StubOcr("transcribed chart text");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Image(Png(alt: "Quarterly Revenue Diagram")));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("**Quarterly Revenue Diagram**");
        var caption = result.Markdown.IndexOf("Quarterly Revenue Diagram", StringComparison.Ordinal);
        var body = result.Markdown.IndexOf("transcribed chart text", StringComparison.Ordinal);
        body.ShouldBeGreaterThan(caption, "the alt-text caption labels the figure block above the transcription");
    }

    [Fact]
    public async Task Marks_incomplete_when_ocr_truncates_a_figure()
    {
        StubOcr("PARTIAL", isComplete: false, reason: "truncated at the token limit");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("PARTIAL");
    }

    [Fact]
    public async Task Skips_decorative_images_below_min_pixels()
    {
        StubOcr("SHOULD_NOT_APPEAR");

        // 300000 EMU ≈ 31 px per side → ~961 px², below the 1000 threshold.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null, extent: 300_000)));

        var result = await CreateExtractor(minImagePixels: 1000).ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Body text");
        result.Markdown.ShouldNotContain("SHOULD_NOT_APPEAR");
        result.IsComplete.ShouldBeTrue();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caps_images_per_file_and_marks_incomplete()
    {
        StubOcr("FIG");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null))
            .Image(Png(alt: null)));

        var result = await CreateExtractor(maxImages: 1).ExtractAsync(new MemoryStream(docx), DocxContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("cap");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_document_text_when_an_embedded_image_OCR_throws()
    {
        _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("vision provider down"));

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("DOCBODYTEXT")
            .Image(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("DOCBODYTEXT");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("OCR");
    }

    [Fact]
    public async Task Skips_vector_images_and_marks_incomplete()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        var emf = new DocxFixtures.ImageSpec(new byte[] { 1, 2, 3, 4 }, "image/x-emf", null);
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(emf));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("decoded");
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_a_mislabeled_image_whose_bytes_are_not_a_known_raster()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // Declared image/png, but the bytes carry no PNG signature (corrupt / mislabeled part).
        var bogus = new DocxFixtures.ImageSpec(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }, "image/png", null);
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec().Paragraph("Body text").Image(bogus));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("decoded");
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_an_oversized_image_and_marks_incomplete()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // A valid PNG whose decompressed size exceeds a tiny per-image byte cap. It must be skipped before
        // full materialization (the ZIP-decompression-bomb guard), not transcribed.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null)));

        var result = await CreateExtractor(maxImageBytes: 50).ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("size cap");
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preserves_soft_line_breaks_without_fusing_text()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .SoftBreak("123 Main St", "Suite 400"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        // The w:br must not fuse the two runs into "123 Main StSuite 400".
        result.Markdown.ShouldNotContain("StSuite");
        result.Markdown.ShouldContain("123 Main St");
        result.Markdown.ShouldContain("Suite 400");
    }

    [Fact]
    public async Task Keeps_separate_paragraphs_as_distinct_markdown_blocks()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Revenue up 10%")
            .Paragraph("Costs flat"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        // Paragraphs are joined with a blank line so they don't collapse into one rendered paragraph.
        result.Markdown.ShouldContain("Revenue up 10%\n\nCosts flat");
    }

    [Fact]
    public async Task Applies_default_language_hints_when_the_context_has_none()
    {
        StubOcr("FIGURE");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null)));

        var extractor = CreateExtractor(ocrOptions: new DocumentAIOcrOptions());
        await extractor.ExtractAsync(new MemoryStream(docx), DocxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.Contains("ja") && o.LanguageHints.Contains("en")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_empty_and_incomplete_for_an_unopenable_file()
    {
        var notDocx = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a docx"));

        var result = await CreateExtractor().ExtractAsync(notDocx, DocxContext());

        // At runtime this provider owns .docx and the orchestrator does not fall through to ElBruno, so an
        // unopenable file is reported as empty + incomplete (the honest #268 signal), not a silent success.
        result.Markdown.ShouldBeNullOrEmpty();
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.ProviderName.ShouldBe(DocxExtractor.ProviderIdentifier);
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deduplicates_alternate_content_textbox_text()
    {
        // A modern Word text box stores the same text in both the mc:Choice (DrawingML wps:txbx) and the
        // mc:Fallback (legacy VML). The MC-collapsing open settings keep only one branch, so the text must
        // appear exactly once — not twice (the duplication the default NoProcess open mode would cause).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .AlternateContentTextBox("TEXTBOX_CONTENT"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        CountOccurrences(result.Markdown, "TEXTBOX_CONTENT").ShouldBe(1);
    }

    [Fact]
    public async Task Transcribes_an_alternate_content_image_only_once()
    {
        StubOcr("FIGURE_ONCE");

        // The same picture (same relationship id) appears in both the mc:Choice and mc:Fallback drawing. MC
        // collapsing keeps only one, so it must be OCR'd once — otherwise the image budget is double-charged
        // and the transcription block is duplicated.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .AlternateContentImage(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        CountOccurrences(result.Markdown, "FIGURE_ONCE").ShouldBe(1);
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

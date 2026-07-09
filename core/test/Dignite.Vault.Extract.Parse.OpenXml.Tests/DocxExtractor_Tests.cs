using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse.OpenXml;

public class DocxExtractor_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private DocxExtractor CreateExtractor(
        int minImagePixels = 0,
        int maxImages = 50,
        long maxImageBytes = 16L * 1024 * 1024)
        => new(
            _ocr,
            Options.Create(new OpenXmlExtractorOptions
            {
                MinImagePixels = minImagePixels,
                MaxImagesPerFile = maxImages,
                MaxImageBytesPerImage = maxImageBytes
            }));

    private static TextExtractionContext DocxContext()
        => new()
        {
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            FileExtension = ".docx"
        };

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
    public async Task Escapes_a_body_paragraph_that_begins_with_a_block_marker()
    {
        // #320 case 1: literal document text that happens to begin with a block marker must not be re-parsed
        // as a heading / list / blockquote / ordered item.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("# Not a heading")
            .Paragraph("- Not a list")
            .Paragraph("> Not a quote")
            .Paragraph("1. Not ordered"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("\\# Not a heading");
        result.Markdown.ShouldContain("\\- Not a list");
        result.Markdown.ShouldContain("\\> Not a quote");
        result.Markdown.ShouldContain("1\\. Not ordered");
    }

    [Fact]
    public async Task Escapes_a_marker_on_a_soft_break_continuation_line()
    {
        // #320 case 2: a w:br continuation that starts with "- " would otherwise split into a sibling list item.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .SoftBreak("Intro line", "- looks like a sub-item"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Intro line\n\\- looks like a sub-item");
    }

    [Fact]
    public async Task Escapes_literal_asterisks_so_an_emphasis_span_is_not_mis_terminated()
    {
        // #320 case 3: a bold run whose text contains '*' must render as "**a\*b**", not the broken "**a*b**".
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Runs(new DocxFixtures.RunSpec("a*b", Bold: true)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("**a\\*b**");
    }

    [Fact]
    public async Task Escapes_inline_brackets_so_a_paragraph_cannot_inject_a_link()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("See [the site](http://evil.example) now"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("See \\[the site\\](http://evil.example) now");
    }

    [Fact]
    public async Task Escapes_special_characters_in_heading_text()
    {
        // #320 case 4: the "# " prefix is generated structure (kept), but a link in the heading TEXT is escaped.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Heading("Clause [9](http://x)", 1));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# Clause \\[9\\](http://x)");
    }

    [Fact]
    public async Task Does_not_over_escape_ordinary_paragraph_text()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("The fee is 3.14 per unit - a fair price."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("The fee is 3.14 per unit - a fair price.");
    }

    [Fact]
    public async Task Escapes_metacharacters_in_an_image_caption()
    {
        // #320/#480: alt-text is author-controlled source text; a bracketed link in it must not become a clickable
        // link. The caption is block-escaped (as on the PDF path) and placed before the *[Image OCR]* marker — no
        // longer bolded.
        StubOcr("TRANSCRIPT");
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Image(Png(alt: "See [details](http://evil.example)")));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("See \\[details\\](http://evil.example)");
        result.Markdown.ShouldNotContain("**See");
    }

    [Fact]
    public async Task Escapes_metacharacters_in_a_hyperlink_label()
    {
        // #320: a hyperlink's display text is source text; a literal '*' in it must not render as emphasis.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .HyperlinkParagraph("click *now*", "http://example.com/page"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[click \\*now\\*](http://example.com/page)");
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

        result.Markdown.ShouldContain("Quarterly Revenue Diagram");
        result.Markdown.ShouldNotContain("**Quarterly Revenue Diagram**");
        var caption = result.Markdown.IndexOf("Quarterly Revenue Diagram", StringComparison.Ordinal);
        var open = result.Markdown.IndexOf("*[Image OCR]*", StringComparison.Ordinal);
        var body = result.Markdown.IndexOf("transcribed chart text", StringComparison.Ordinal);
        caption.ShouldBeGreaterThanOrEqualTo(0);
        open.ShouldBeGreaterThan(caption, "the caption labels the figure block above the open marker");
        body.ShouldBeGreaterThan(open, "the transcription sits inside the markers, below the caption");
    }

    [Fact]
    public async Task Wraps_the_figure_transcription_in_ImageOcrMarkup_markers()
    {
        // #480: the deterministic segmentation trigger keys on ImageOcrMarkup.Contains(Document.Markdown). Before
        // #480 OpenXML figures carried no markers, so a Word-embedded standalone document (e.g. a pasted invoice
        // photo) was never deterministically routed — only the classifier's subjective flag could catch it, exactly
        // the single-leg unreliability #379 fixed for the PDF path. DOCX is a flow document, so the marker is the
        // bare page-less form, byte-compatible with the PDF path.
        StubOcr("INVOICE TOTAL 42");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Cover letter")
            .Image(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("*[Image OCR]*");
        result.Markdown.ShouldContain("*[End OCR]*");
        ImageOcrMarkup.Contains(result.Markdown).ShouldBeTrue();
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
    public async Task Passes_empty_language_hints_to_figure_ocr_when_the_context_has_none()
    {
        StubOcr("FIGURE");

        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .Image(Png(alt: null)));

        // #441: no central host default. With no per-document hints, the figure path passes empty hints;
        // a provider that needs a default reads its own config (e.g. PaddleOcr:Languages).
        var extractor = CreateExtractor();
        await extractor.ExtractAsync(new MemoryStream(docx), DocxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.Count == 0),
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

    [Fact]
    public async Task Renders_table_as_a_markdown_table()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Table(new IReadOnlyList<string>[]
            {
                new[] { "Name", "Amount" },
                new[] { "Widget", "42" }
            }));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("| Name | Amount |");
        result.Markdown.ShouldContain("| --- | --- |");
        result.Markdown.ShouldContain("| Widget | 42 |");
    }

    [Fact]
    public async Task Renders_a_ragged_table_as_a_rectangular_markdown_table()
    {
        // First row is a single cell; data rows have 3 columns. The separator and every row must use the
        // widest row's column count, or the Markdown table renders broken (mirrors the PPTX renderer).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Table(new IReadOnlyList<string>[]
            {
                new[] { "Summary" },
                new[] { "A", "B", "C" },
                new[] { "1", "2", "3" }
            }));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("| --- | --- | --- |");
        result.Markdown.ShouldContain("| Summary |  |  |");
        result.Markdown.ShouldContain("| A | B | C |");
    }

    [Fact]
    public async Task Renders_bold_and_italic_runs()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Runs(
                new DocxFixtures.RunSpec("normal "),
                new DocxFixtures.RunSpec("bold", Bold: true),
                new DocxFixtures.RunSpec(" and "),
                new DocxFixtures.RunSpec("italic", Italic: true)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("normal **bold** and *italic*");
    }

    [Fact]
    public async Task Merges_consecutive_same_format_runs()
    {
        // Word often splits a styled span across several runs; the renderer must not emit **Hel****lo**.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Runs(
                new DocxFixtures.RunSpec("Hel", Bold: true),
                new DocxFixtures.RunSpec("lo", Bold: true)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("**Hello**");
        result.Markdown.ShouldNotContain("****");
    }

    [Fact]
    public async Task Renders_combined_bold_italic()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Runs(new DocxFixtures.RunSpec("both", Bold: true, Italic: true)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("***both***");
    }

    [Fact]
    public async Task Moves_whitespace_outside_emphasis_markers()
    {
        // CommonMark does not render "** mid **"; the spaces must sit outside the markers.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Runs(
                new DocxFixtures.RunSpec("a"),
                new DocxFixtures.RunSpec(" mid ", Bold: true),
                new DocxFixtures.RunSpec("b")));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("a **mid** b");
    }

    [Fact]
    public async Task Renders_a_hyperlink_as_a_markdown_link()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .HyperlinkParagraph("Anthropic", "https://www.anthropic.com/"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[Anthropic](https://www.anthropic.com/)");
    }

    [Fact]
    public async Task Renders_a_hyperlink_with_spaces_in_the_url_using_angle_brackets()
    {
        // System.Uri.ToString() decodes %20 back to a literal space; the bare (url) form would then NOT
        // render as a link in CommonMark, so a URL with whitespace must use the angle-bracket form.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .HyperlinkParagraph("docs", "https://e.com/a b"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[docs](<https://e.com/a b>)");
    }

    [Fact]
    public async Task Keeps_inserted_revision_text_and_drops_deleted_revision_text()
    {
        // Accepted view of tracked changes: w:ins runs are kept, w:del runs (w:delText) are excluded.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .TrackedChangesParagraph(before: "Kept ", inserted: "added", deleted: "removed"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Kept");
        result.Markdown.ShouldContain("added");
        result.Markdown.ShouldNotContain("removed");
    }

    [Fact]
    public async Task Heading_with_a_text_box_does_not_duplicate_or_glue_the_text_box_text()
    {
        // A text box anchored to a heading paragraph must not be folded into the heading line (which would
        // glue it on as "# HEAD_TITLETB_CONTENT") and must not be emitted both in the heading and as its own
        // block. The heading stays clean; the text box appears exactly once (as its own block).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .HeadingWithTextBox("HEAD_TITLE", "TB_CONTENT"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# HEAD_TITLE");
        result.Markdown.ShouldNotContain("HEAD_TITLETB_CONTENT");
        CountOccurrences(result.Markdown, "TB_CONTENT").ShouldBe(1);
    }

    [Fact]
    public async Task Renders_chart_backing_data_as_a_markdown_table()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Chart("Quarterly Revenue", new[] { "Q1", "Q2" }, "Revenue", new[] { "10", "20" }));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Quarterly Revenue");
        result.Markdown.ShouldContain("| Category | Revenue |");
        result.Markdown.ShouldContain("| Q1 | 10 |");
        result.Markdown.ShouldContain("| Q2 | 20 |");
        result.IsComplete.ShouldBeTrue();
        // Charts are pure structured extraction from the format — no OCR call.
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renders_bullet_list_items()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .BulletItem("First")
            .BulletItem("Second"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("- First");
        result.Markdown.ShouldContain("- Second");
    }

    [Fact]
    public async Task Renders_ordered_list_items()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .OrderedItem("Step one")
            .OrderedItem("Step two"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("1. Step one");
        result.Markdown.ShouldContain("1. Step two");
    }

    [Fact]
    public async Task Indents_nested_list_items_by_level()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .BulletItem("Top", level: 0)
            .BulletItem("Nested", level: 1)
            .BulletItem("Deeper", level: 2));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        // Three spaces of indentation per nesting level (>= the widest marker "1. ", so ordered nesting works too).
        result.Markdown.ShouldContain("- Top");
        result.Markdown.ShouldContain("   - Nested");
        result.Markdown.ShouldContain("      - Deeper");
    }

    [Fact]
    public async Task Indents_a_child_under_an_ordered_parent_enough_to_nest()
    {
        // An ordered marker "1. " is 3 chars wide, so a child needs >= 3 spaces to be recognized as nested by
        // CommonMark; 2 spaces would make it a sibling and split the ordered list (restarting "Second" at 1).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .OrderedItem("First", level: 0)
            .BulletItem("sub", level: 1)
            .OrderedItem("Second", level: 0));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("1. First");
        result.Markdown.ShouldContain("   - sub");
        result.Markdown.ShouldContain("1. Second");
    }

    [Fact]
    public async Task Extracts_an_image_inside_a_table_cell()
    {
        StubOcr("CELL_FIGURE");

        // A Markdown table cell can't host a transcription block, so an image inside a cell is extracted as
        // its own block after the table — not silently dropped (which would also leave IsComplete wrongly true).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .TableWithImageInCell(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("CELL_FIGURE");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Extracts_content_control_body()
    {
        // A block-level content control (w:sdt) — common in Word forms/templates — must not silently drop
        // its wrapped paragraph; it should appear in the Markdown and the result stays complete.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .ContentControl("FORM_FIELD_TEXT"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("FORM_FIELD_TEXT");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Transcribes_a_legacy_vml_raster_image()
    {
        StubOcr("VML_FIGURE");

        // A compatibility-mode / legacy DOCX can carry a PNG/JPEG via VML (w:pict/v:imagedata) instead of
        // DrawingML; it must still be OCR'd through the IOcrProvider, not silently dropped.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Body text")
            .VmlImage(Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("VML_FIGURE");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_bullet_when_the_list_level_is_undefined()
    {
        // numId 3's abstract definition only defines level 0; a list item at level 1 has no matching level
        // definition and must default to a neutral bullet — not silently inherit level 0's "decimal" (which
        // would render the item as an ordered "1.").
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .DanglingLevelItem("Orphan", level: 1));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("- Orphan");
        result.Markdown.ShouldNotContain("1. Orphan");
    }

    [Fact]
    public async Task Extracts_content_control_text_inside_a_table_cell()
    {
        // A cell whose paragraph is wrapped in a content control (w:sdt) — common in form templates — must
        // not render empty; its text must appear in the table.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .TableWithContentControlCell("CELL_FORM_FIELD"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("CELL_FORM_FIELD");
    }

    [Fact]
    public async Task Caps_content_control_nesting_depth_without_crashing()
    {
        // Pathologically deep content-control nesting must not StackOverflow (uncatchable -> kills the
        // worker); the walk stops at the depth cap and marks incomplete instead of crashing.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .DeeplyNestedContentControl(depth: 40, text: "DEEP"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.ShouldNotBeNull();
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Transcribes_a_text_box_image_only_once()
    {
        StubOcr("BOX_FIGURE");

        // An image embedded inside a text box must be OCR'd exactly once — the recursive drawing walk must
        // not transcribe both the text-box drawing and the nested image drawing (double cost + double budget
        // + duplicate block). The text box's own text is still emitted.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .TextBoxWithImage("box text", Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        CountOccurrences(result.Markdown, "BOX_FIGURE").ShouldBe(1);
        result.Markdown.ShouldContain("box text");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clamps_an_extreme_list_level()
    {
        // A malformed / attacker w:ilvl must not blow up the indent string (level * 3 could overflow or
        // allocate hundreds of MB). The level is clamped; the item still renders and the result stays complete.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .BulletItem("Item", level: int.MaxValue));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("- Item");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Surfaces_footnote_and_endnote_markers_and_bodies()
    {
        // #315: an in-text marker at the reference position, and the resolved note body appended at the end.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Footnote("Body with a footnote", 2, "The footnote text.")
            .Endnote("Body with an endnote", 3, "The endnote text."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        // Markers sit at the reference's reading position (right after the run text).
        result.Markdown.ShouldContain("Body with a footnote[^fn2]");
        result.Markdown.ShouldContain("Body with an endnote[^en3]");
        // The resolved bodies are appended as Markdown-footnote definitions.
        result.Markdown.ShouldContain("[^fn2]: The footnote text.");
        result.Markdown.ShouldContain("[^en3]: The endnote text.");
        // The definitions come AFTER the body text they annotate.
        result.Markdown.IndexOf("[^fn2]:", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.Markdown.IndexOf("Body with a footnote", StringComparison.Ordinal));
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Indents_the_continuation_of_a_multi_paragraph_footnote_body()
    {
        // #315: a footnote body with two paragraphs. The definition's continuation paragraph must be indented
        // four spaces so it stays part of the [^fn2] definition (Markdown footnote syntax) rather than
        // escaping flush-left as an ordinary document paragraph (which could also wedge between definitions).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Intro")
            .Footnote("Body with a footnote", 2, "First note paragraph.\n\nSecond note paragraph."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[^fn2]: First note paragraph.");
        // The continuation paragraph is indented four spaces (attached to the definition), never flush-left.
        result.Markdown.ShouldContain("\n    Second note paragraph.");
        result.Markdown.ShouldNotContain("\nSecond note paragraph.");
    }

    [Fact]
    public async Task Captures_a_footnote_anchored_in_a_heading()
    {
        // #315: a footnote reference inside a HeadingN. The plain-text heading path used to drop both the
        // marker and the note body silently, bypassing #268. Now the heading carries the marker and the body
        // is defined at the document end.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .HeadingFootnote("Chapter One", 2, "Heading note body.", level: 1));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("# Chapter One[^fn2]");
        result.Markdown.ShouldContain("[^fn2]: Heading note body.");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Captures_a_footnote_anchored_in_a_table_cell()
    {
        // #315: a footnote reference inside a table cell. The cell path used to drop both the marker and the
        // body silently, bypassing #268. Now the marker is appended to the cell text and the body is defined.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .FootnoteInTableCell("Cell value", 2, "Cell note body."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Cell value[^fn2]");
        result.Markdown.ShouldContain("[^fn2]: Cell note body.");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_document_without_notes_emits_no_note_markers()
    {
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec().Paragraph("Plain body, no notes."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Plain body, no notes.");
        result.Markdown.ShouldNotContain("[^");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Excludes_separator_notes_from_output()
    {
        // The FootnotesPart carries the auto-inserted separator / continuationSeparator notes (sentinel text);
        // only the referenced author note is emitted, never the separators.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Footnote("See note", 2, "Author footnote."));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[^fn2]: Author footnote.");
        result.Markdown.ShouldNotContain("SEP_SENTINEL");
        result.Markdown.ShouldNotContain("CONT_SENTINEL");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Marks_incomplete_for_a_dangling_note_reference()
    {
        // A footnote ref to id 99 with a FootnotesPart present (real note 2 + separators) but no id 99 —
        // dangling, so the completeness signal trips rather than silently dropping the note.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Footnote("Real", 2, "Real note.")
            .DanglingFootnote("Dangling", 99));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[^fn2]: Real note.");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("footnote/endnote reference");
    }

    [Fact]
    public async Task Marks_incomplete_when_the_notes_part_is_missing()
    {
        // A footnote reference with NO FootnotesPart at all (missing part) — dangling, tripping #268. The
        // in-text marker is still emitted at its reading position.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .DanglingFootnote("Orphan ref", 2));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("Orphan ref[^fn2]");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("footnote/endnote reference");
    }

    [Fact]
    public async Task Resolves_a_footnote_body_hyperlink_against_the_footnotes_part()
    {
        // #457: a hyperlink inside a footnote body is a relationship of the FootnotesPart, not the main part.
        // Here the r:id exists ONLY in the FootnotesPart, so resolving against the main part (the old bug)
        // finds nothing and drops the URL, degrading the link to plain text.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Intro")
            .FootnoteWithBodyLink("See note", 2, "Reference: ", "the source", "https://notes.example/fn-source"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[^fn2]: Reference: [the source](https://notes.example/fn-source)");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Resolves_an_endnote_body_hyperlink_against_its_own_part_when_the_id_collides_with_the_main_part()
    {
        // #457: the endnote body's r:id ALSO exists in the main part (a legitimate collision — the two parts
        // have independent relationship spaces) pointing at a DIFFERENT target. The note link must resolve to
        // the EndnotesPart's target, never the main document's decoy (the old bug resolved to the decoy).
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .Paragraph("Intro")
            .EndnoteWithBodyLink(
                "See endnote", 3, "Cited at ", "the archive",
                linkUrl: "https://notes.example/en-archive",
                mainDecoyUrl: "https://main.example/decoy"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("[^en3]: Cited at [the archive](https://notes.example/en-archive)");
        result.Markdown.ShouldNotContain("https://main.example/decoy");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Transcribes_every_image_in_a_grouped_drawing()
    {
        StubOcr("GROUPED_FIG");

        // #322: a grouped drawing (wpg:wgp) with two pictures. The old Descendants<Blip>().FirstOrDefault()
        // walk transcribed only the first; iterating pic:pic transcribes each.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .GroupedImages(Png(alt: null), Png(alt: null)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        await _ocr.Received(2).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        CountOccurrences(result.Markdown, "GROUPED_FIG").ShouldBe(2);
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Transcribes_a_shape_picture_fill_image()
    {
        StubOcr("FILL_FIG");

        // #322 regression: a shape whose fill is a picture (wps:wsp/a:blipFill/a:blip) has no pic:pic, so the
        // pic:pic walk missed it and it was dropped silently. It carries real image content, so it must be
        // transcribed via the shape's own extent + alt-text.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .ShapeFillImage(Png(alt: null), altText: "FILL_ALT"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldContain("FILL_FIG");
        result.Markdown.ShouldContain("FILL_ALT");          // #480: caption present, no longer bolded
        result.Markdown.ShouldNotContain("**FILL_ALT**");
    }

    [Fact]
    public async Task Uses_the_images_own_caption_for_a_text_box_image()
    {
        StubOcr("BOX_FIG");

        // #322: a text-box image's caption must come from the image's OWN wp:inline/docPr (descr "IMAGE_ALT"),
        // not the outer text box's docPr (which carries no descr). Before the pic:pic refactor the outer
        // drawing's docPr was read, so the image's alt-text was lost.
        var docx = DocxFixtures.Build(new DocxFixtures.DocSpec()
            .TextBoxWithImage("box text", Png(alt: "IMAGE_ALT")));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(docx), DocxContext());

        result.Markdown.ShouldContain("IMAGE_ALT");         // #480: caption present, no longer bolded
        result.Markdown.ShouldNotContain("**IMAGE_ALT**");
        CountOccurrences(result.Markdown, "BOX_FIG").ShouldBe(1);
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

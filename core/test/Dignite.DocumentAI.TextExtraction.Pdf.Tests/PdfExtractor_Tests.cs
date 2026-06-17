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
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

public class PdfExtractor_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private PdfExtractor CreateExtractor(
        int minImagePixels = 0,
        int maxImagesPerPdf = 50,
        bool skipFullPageScanBackground = true,
        DocumentAIOcrOptions? ocrOptions = null)
        => new(
            _ocr,
            Options.Create(new PdfExtractorOptions
            {
                MinImagePixels = minImagePixels,
                MaxImagesPerPdf = maxImagesPerPdf,
                // Defaults to true in production; kept explicit here so each test states the behavior it
                // exercises. The remaining FullPageScan* thresholds use their production defaults.
                SkipFullPageScanBackground = skipFullPageScanBackground
            }),
            // Default to empty hints so unrelated tests don't get defaults injected unexpectedly.
            Options.Create(ocrOptions ?? new DocumentAIOcrOptions { DefaultLanguageHints = new List<string>() }));

    // A full-page raster inset 10pt inside the fixture page — clears the coverage bar on both axes
    // (~0.97). Derived from PdfFixtures' page size so a fixture page-size change can't silently flip the
    // skip/keep outcome of these geometry-sensitive tests.
    private static readonly PdfRectangle FullPageRect = new(
        10, 10, PdfFixtures.PageWidth - 10, PdfFixtures.PageHeight - 10);

    // N text baselines spread evenly across the page height — a whole-page transcription, not a caption.
    private static (string Text, double BaselineY)[] SpreadLines(int count, double topY = 800, double bottomY = 60)
    {
        var lines = new (string, double)[count];
        var step = count > 1 ? (topY - bottomY) / (count - 1) : 0;
        for (var i = 0; i < count; i++)
        {
            lines[i] = ($"LINE{i:D2} scanned body text", topY - (i * step));
        }

        return lines;
    }

    private static TextExtractionContext PdfContext()
        => new() { ContentType = "application/pdf", FileExtension = ".pdf" };

    private void StubOcr(string markdown, bool isComplete = true, string? reason = null)
        => _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new OcrResult
            {
                Markdown = markdown,
                ProviderName = "FakeOcr",
                IsComplete = isComplete,
                IncompleteReason = reason
            });

    [Fact]
    public async Task Inlines_image_transcription_at_its_reading_position()
    {
        StubOcr("BRAVO");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("ALPHA", 760.0), ("CHARLIE", 100.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        var alpha = result.Markdown.IndexOf("ALPHA", StringComparison.Ordinal);
        var bravo = result.Markdown.IndexOf("BRAVO", StringComparison.Ordinal);
        var charlie = result.Markdown.IndexOf("CHARLIE", StringComparison.Ordinal);

        alpha.ShouldBeGreaterThanOrEqualTo(0);
        bravo.ShouldBeGreaterThan(alpha, "the figure transcription must come after the text above it");
        charlie.ShouldBeGreaterThan(bravo, "the figure transcription must come before the text below it");

        // Primary text is the digital text layer; figures used OCR but this is a digital extraction.
        result.UsedOcr.ShouldBeFalse();
        result.ProviderName.ShouldBe(PdfExtractor.ProviderIdentifier);
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Surfaces_each_transcribed_image_as_an_out_of_band_figure()
    {
        StubOcr("INVOICE No. 42");

        var png = TinyPng.CreateSolid(48, 48);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // #306 out-of-band signal: the transcribed image is surfaced on Figures with its bytes + provenance,
        // while UsedOcr stays false (a digital extraction) and FigureOcrCount records the embedded-image OCR.
        result.UsedOcr.ShouldBeFalse();
        result.FigureOcrCount.ShouldBe(1);

        result.Figures.ShouldNotBeNull();
        result.Figures!.Count.ShouldBe(1);
        var figure = result.Figures[0];
        figure.Transcription.ShouldBe("INVOICE No. 42");
        figure.Content.Length.ShouldBeGreaterThan(0);
        figure.ContentType.ShouldStartWith("image/");
        figure.PageNumber.ShouldBe(1);
    }

    [Fact]
    public async Task Leaves_figures_null_and_count_zero_when_no_image_is_transcribed()
    {
        var pdf = PdfFixtures.Build(texts: new[] { ("Just digital text", 700.0) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Null (not empty) so a downstream "has figures?" check stays a simple null test; no OCR ran.
        result.Figures.ShouldBeNull();
        result.FigureOcrCount.ShouldBe(0);
    }

    [Fact]
    public async Task Counts_a_dispatched_figure_ocr_even_when_it_throws()
    {
        // The OCR call is dispatched (bytes sent) then throws (provider timeout / rate-limit). FigureOcrCount
        // is a cost-attribution signal that counts dispatched calls, so the failed call must still be counted.
        _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var png = TinyPng.CreateSolid(48, 48);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.FigureOcrCount.ShouldBe(1);   // dispatched, even though it threw
        result.Figures.ShouldBeNull();       // no transcription produced -> no out-of-band figure
        result.IsComplete.ShouldBeFalse();   // #268 trips on the failed figure OCR
    }

    [Fact]
    public async Task Feeds_image_bytes_to_the_ocr_provider()
    {
        StubOcr("FIGURE");

        var png = TinyPng.CreateSolid(48, 48);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Exactly one image → exactly one OCR call, with an image content type (transcription only).
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.ContentType.StartsWith("image/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_incomplete_when_ocr_truncates_a_figure()
    {
        StubOcr("PARTIAL", isComplete: false, reason: "truncated at the token limit");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("PARTIAL");
    }

    [Fact]
    public async Task Returns_empty_and_skips_ocr_when_pdf_has_no_text_layer()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // Scanned / image-only PDF: a page with an image but no digital text layer. PdfExtractor must NOT
        // OCR images here — it returns empty so DefaultTextExtractor's whole-page OCR fallback owns it.
        var png = TinyPng.CreateSolid(80, 80);
        var pdf = PdfFixtures.Build(
            texts: Array.Empty<(string, double)>(),
            images: new[] { (png, new PdfRectangle(50, 300, 400, 700)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldBeNullOrEmpty();
        result.ProviderName.ShouldBe(PdfExtractor.ProviderIdentifier);
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_decorative_images_below_min_pixels()
    {
        StubOcr("SHOULD_NOT_APPEAR");

        var icon = TinyPng.CreateSolid(8, 8); // 64 px, below the threshold
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (icon, new PdfRectangle(50, 400, 60, 410)) });

        var result = await CreateExtractor(minImagePixels: 1000).ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("Body text");
        result.Markdown.ShouldNotContain("SHOULD_NOT_APPEAR");
        // Decorative images are not figure content → not counted against completeness.
        result.IsComplete.ShouldBeTrue();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caps_images_per_document_and_marks_incomplete()
    {
        StubOcr("FIG");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[]
            {
                (png, new PdfRectangle(50, 500, 200, 650)),
                (png, new PdfRectangle(50, 200, 200, 350))
            });

        var result = await CreateExtractor(maxImagesPerPdf: 1).ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("cap");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_text_layer_when_an_embedded_image_OCR_throws()
    {
        // A figure's OCR failing (provider timeout / rate-limit / auth / one bad image) must degrade,
        // not nuke the whole digital extraction: the digital text layer is the primary payload, figure
        // OCR is the auxiliary add-on.
        _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("vision provider down"));

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("DIGITALBODYTEXT", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        // Must NOT throw — the whole extraction does not fail because one figure's OCR did.
        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("DIGITALBODYTEXT"); // digital text layer preserved
        result.IsComplete.ShouldBeFalse();                // #268 signal tripped
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("OCR");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_default_language_hints_when_the_context_has_none()
    {
        StubOcr("FIGURE");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        // Context carries no hints; the host default {ja,en} must be applied (same as the whole-page path).
        var extractor = CreateExtractor(ocrOptions: new DocumentAIOcrOptions());
        await extractor.ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.Contains("ja") && o.LanguageHints.Contains("en")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_empty_for_a_non_pdf_stream()
    {
        var notPdf = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a pdf"));

        var result = await CreateExtractor().ExtractAsync(notPdf, PdfContext());

        // Open failed → empty Markdown so the orchestrator's OCR fallback can try.
        result.Markdown.ShouldBeNullOrEmpty();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_the_full_page_scan_background_of_a_sandwich_pdf()
    {
        // Searchable / "sandwich" PDF (#309): a full-page scan raster + a whole-page OCR text layer. The
        // raster must NOT be re-OCR'd (it would duplicate the text and burn a paid vision call); the text
        // layer is kept and the result stays complete (intentional skip ≠ lost figure).
        StubOcr("SHOULD_NOT_BE_OCRED");

        var png = TinyPng.CreateSolid(120, 160);
        var pdf = PdfFixtures.Build(
            texts: SpreadLines(20), // ≥ FullPageScanMinTextLines (15) → a whole-page (visible) transcription
            images: new[] { (png, FullPageRect) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldNotContain("SHOULD_NOT_BE_OCRED");
        result.Markdown.ShouldContain("LINE00 scanned body text"); // text layer preserved
        result.Markdown.ShouldContain("LINE19 scanned body text");
        // A deliberate skip is non-lossy — it must NOT trip the #268 completeness signal.
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Skips_a_sparse_invisible_sandwich_via_the_tr3_signal()
    {
        // The Tr 3 bonus: even with only a few lines (below FullPageScanMinTextLines), a predominantly
        // invisible text layer over a full-page raster is the canonical sandwich signature → still skipped.
        StubOcr("SHOULD_NOT_BE_OCRED");

        var png = TinyPng.CreateSolid(120, 160);
        var pdf = PdfFixtures.Build(
            texts: SpreadLines(3),                       // 3 lines < FullPageScanMinTextLines (5)
            images: new[] { (png, FullPageRect) },
            textRenderingMode: TextRenderingMode.Neither); // invisible OCR layer (Tr 3)

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldContain("LINE00 scanned body text"); // invisible text is still extracted
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Transcribes_a_full_page_figure_with_a_caption_block()
    {
        // A digital PDF whose full-page image is a real figure with only a caption block looks identical to
        // a sandwich under "big image + has text", but the text is a thin band at one edge — it does NOT
        // span the image vertically → the figure must be kept and transcribed (NOT dropped). This is the
        // false-positive the guard must avoid.
        StubOcr("ARCHITECTURE DIAGRAM");

        var png = TinyPng.CreateSolid(120, 160);
        // A multi-line caption clustered at the bottom: its words DO fall inside the image region (so the
        // figure is kept by the vertical-coverage guard, not by the empty-region early-out), but the band
        // spans only a sliver of the image height.
        var pdf = PdfFixtures.Build(
            texts: new[]
            {
                ("Figure 1: System architecture", 70.0),
                ("Source: internal benchmarking, 2026", 52.0),
                ("All values normalized to the baseline", 34.0)
            },
            images: new[] { (png, FullPageRect) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldContain("ARCHITECTURE DIAGRAM"); // figure transcribed and inlined
        result.Markdown.ShouldContain("Figure 1: System architecture");
        // In-region caption text is present, proving the figure was kept via low vertical coverage — not
        // because the region happened to contain no words.
        result.Markdown.ShouldContain("Source: internal benchmarking, 2026");
    }

    [Fact]
    public async Task Keeps_and_transcribes_a_small_embedded_figure()
    {
        // #301 behavior is unchanged for ordinary embedded figures: a small image (well below the full-page
        // coverage bar) is transcribed as before, regardless of the sandwich guard.
        StubOcr("SMALL FIGURE");

        var png = TinyPng.CreateSolid(48, 48);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) }); // ~25%×18% of the page

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldContain("SMALL FIGURE");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Transcribes_the_full_page_image_when_the_skip_guard_is_disabled()
    {
        // Control / opt-out: with SkipFullPageScanBackground = false the very same sandwich page is OCR'd
        // (the unconditional #301 path), proving the image is genuinely full-page and the skip elsewhere is
        // the guard's doing — not the fixture failing to embed an image.
        StubOcr("RE_OCRED_BACKGROUND");

        var png = TinyPng.CreateSolid(120, 160);
        var pdf = PdfFixtures.Build(
            texts: SpreadLines(20), // the same sandwich page as the skip test above
            images: new[] { (png, FullPageRect) });

        var result = await CreateExtractor(skipFullPageScanBackground: false)
            .ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldContain("RE_OCRED_BACKGROUND");
    }

    [Fact]
    public async Task Two_column_label_body_page_keeps_the_right_column_sentence_contiguous()
    {
        // #310 Phase A: a left-column label sits vertically between the two wrapped lines of a
        // right-column body sentence. A flat top→bottom sort interleaves the label into the sentence
        // ("alpha beta LABEL gamma delta"); column-aware segmentation must keep the body contiguous.
        var pdf = PdfFixtures.BuildPositioned(new[]
        {
            ("LABEL", 50.0, 503.0),       // left column
            ("alpha beta", 300.0, 508.0), // right column, first wrapped line (just above the label)
            ("gamma delta", 300.0, 498.0) // right column, second wrapped line (just below the label)
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // All content is present (non-lossy).
        result.Markdown.ShouldContain("LABEL");
        result.Markdown.ShouldContain("alpha beta");
        result.Markdown.ShouldContain("gamma delta");

        // The body sentence is not interrupted by the label: nothing between its first and last word is
        // the label. (The flat sort would place "LABEL" between "beta" and "gamma".)
        var firstBodyWord = result.Markdown.IndexOf("alpha", StringComparison.Ordinal);
        var lastBodyWord = result.Markdown.IndexOf("delta", StringComparison.Ordinal);
        firstBodyWord.ShouldBeGreaterThanOrEqualTo(0);
        lastBodyWord.ShouldBeGreaterThan(firstBodyWord);
        // The left-column label must not be spliced into the right-column sentence.
        result.Markdown
            .Substring(firstBodyWord, lastBodyWord - firstBodyWord)
            .ShouldNotContain("LABEL");
    }

    [Fact]
    public async Task Reconstructs_a_multi_column_table_into_markdown_table_rows()
    {
        // #310 Phase B end-to-end: a positioned multi-column fee schedule whose third-column cell wraps to a
        // second visual line. Phase A would linearize the row's cells as separate paragraphs and split the
        // wrapped cell; the table path must emit GFM table rows with the wrapped cell kept whole, while the
        // title above the table stays its own (non-table) region.
        var pdf = PdfFixtures.BuildPositioned(new[]
        {
            ("Service Fee Schedule", 50.0, 770.0), // title above the table (separate region)

            ("Service", 50.0, 700.0), ("Fee", 230.0, 700.0), ("Note", 380.0, 700.0),
            ("Basic", 50.0, 672.0), ("100", 230.0, 672.0), ("standard", 380.0, 672.0),
            ("support", 380.0, 658.0), // wrapped continuation of the Note cell (single-line pitch below)
            ("Premium", 50.0, 630.0), ("200", 230.0, 630.0), ("priority", 380.0, 630.0)
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // A GFM table was produced.
        result.Markdown.ShouldContain("| --- |");
        // The wrapped Note cell stays intact in its own cell (not split across siblings).
        result.Markdown.ShouldContain("standard support");
        // All table content is present (non-lossy), and the title is preserved as its own region.
        foreach (var token in new[]
                 {
                     "Service Fee Schedule", "Service", "Fee", "Note",
                     "Basic", "100", "Premium", "200", "priority"
                 })
        {
            result.Markdown.ShouldContain(token);
        }
    }

    [Fact]
    public async Task Reconstructs_a_tight_row_pitch_table()
    {
        // #329 review (efficacy): the multi-column fixture above uses a generous row pitch that RecursiveXYCut
        // cuts into per-cell blocks; a tight row pitch is instead cut into per-COLUMN blocks (each column one
        // tall block of stacked rows). Because reconstruction works at the WORD level, not the block level,
        // the grid is recovered either way — a tight pitch is not just non-lossy but a real table.
        var pdf = PdfFixtures.BuildPositioned(new[]
        {
            ("Item", 50.0, 700.0), ("Price", 230.0, 700.0),
            ("Apple", 50.0, 688.0), ("100", 230.0, 688.0),
            ("Pear", 50.0, 676.0), ("200", 230.0, 676.0),
            ("Plum", 50.0, 664.0), ("300", 230.0, 664.0)
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("| --- |"); // a real table, not a paragraph degrade

        foreach (var token in new[] { "Item", "Price", "Apple", "100", "Pear", "200", "Plum", "300" })
        {
            result.Markdown.ShouldContain(token);
        }
    }
}

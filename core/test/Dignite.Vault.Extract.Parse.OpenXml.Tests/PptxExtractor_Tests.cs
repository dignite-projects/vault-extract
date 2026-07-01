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

public class PptxExtractor_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private PptxExtractor CreateExtractor(
        int minImagePixels = 0,
        int maxImages = 50,
        bool includeNotes = true,
        long maxImageBytes = 16L * 1024 * 1024)
        => new(
            _ocr,
            Options.Create(new OpenXmlExtractorOptions
            {
                MinImagePixels = minImagePixels,
                MaxImagesPerFile = maxImages,
                MaxImageBytesPerImage = maxImageBytes,
                IncludeSpeakerNotes = includeNotes
            }));

    private static TextExtractionContext PptxContext()
        => new() { ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation", FileExtension = ".pptx" };

    private void StubOcr(string markdown, bool isComplete = true, string? reason = null)
        => _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new OcrResult
            {
                Markdown = markdown,
                ProviderName = "FakeOcr",
                IsComplete = isComplete,
                IncompleteReason = reason
            });

    private static PptxFixtures.ImageSpec Png(string? alt, long x, long y, long extent = 914400)
        => new(TinyPng.CreateSolid(48, 48), "image/png", alt, x, y, extent, extent);

    [Fact]
    public async Task Escapes_markdown_metacharacters_in_slide_text()
    {
        // #320: literal slide text is escaped; the generated "## " title prefix is kept as real structure.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("# Roadmap", isTitle: true, x: 100, y: 100)
            .Text("- 50% *growth* in [Q1](http://x)", x: 100, y: 1_000_000));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("## \\# Roadmap");
        result.Markdown.ShouldContain("\\- 50% \\*growth\\* in \\[Q1\\](http://x)");
    }

    [Fact]
    public async Task Escapes_metacharacters_in_an_image_caption()
    {
        // #320: PPTX picture alt-text is escaped like the DOCX/PDF captions, so it can't inject a link.
        StubOcr("TRANSCRIPT");
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Image(Png(alt: "See [details](http://evil.example)", x: 100, y: 100)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("**See \\[details\\](http://evil.example)**");
    }

    [Fact]
    public async Task Inlines_image_transcription_at_its_reading_position()
    {
        StubOcr("BRAVO");

        // EMU Y increases downward, so smaller Y is higher on the slide.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("ALPHA", y: 100)
            .Image(Png(alt: null, x: 100, y: 2_000_000))
            .Text("CHARLIE", y: 4_000_000));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        var alpha = result.Markdown.IndexOf("ALPHA", StringComparison.Ordinal);
        var bravo = result.Markdown.IndexOf("BRAVO", StringComparison.Ordinal);
        var charlie = result.Markdown.IndexOf("CHARLIE", StringComparison.Ordinal);

        alpha.ShouldBeGreaterThanOrEqualTo(0);
        bravo.ShouldBeGreaterThan(alpha, "the figure transcription must come after the text above it");
        charlie.ShouldBeGreaterThan(bravo, "the figure transcription must come before the text below it");

        result.UsedOcr.ShouldBeFalse();
        result.ProviderName.ShouldBe(PptxExtractor.ProviderIdentifier);
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Feeds_image_bytes_to_the_ocr_provider()
    {
        StubOcr("FIGURE");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000)));

        await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.ContentType.StartsWith("image/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uses_native_alt_text_as_caption()
    {
        StubOcr("transcribed chart text");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Image(Png(alt: "Quarterly Revenue Diagram", x: 100, y: 1_000_000)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("**Quarterly Revenue Diagram**");
        var caption = result.Markdown.IndexOf("Quarterly Revenue Diagram", StringComparison.Ordinal);
        var body = result.Markdown.IndexOf("transcribed chart text", StringComparison.Ordinal);
        body.ShouldBeGreaterThan(caption, "the alt-text caption labels the figure block above the transcription");
    }

    [Fact]
    public async Task Marks_incomplete_when_ocr_truncates_a_figure()
    {
        StubOcr("PARTIAL", isComplete: false, reason: "truncated at the token limit");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("PARTIAL");
    }

    [Fact]
    public async Task Skips_decorative_images_below_min_pixels()
    {
        StubOcr("SHOULD_NOT_APPEAR");

        // 300000 EMU ≈ 31 px per side → ~961 px², below the 1000 threshold.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000, extent: 300_000)));

        var result = await CreateExtractor(minImagePixels: 1000).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Body text");
        result.Markdown.ShouldNotContain("SHOULD_NOT_APPEAR");
        result.IsComplete.ShouldBeTrue();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caps_images_per_presentation_and_marks_incomplete()
    {
        StubOcr("FIG");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000))
            .Image(Png(alt: null, x: 100, y: 2_000_000)));

        var result = await CreateExtractor(maxImages: 1).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("cap");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_slide_text_when_an_embedded_image_OCR_throws()
    {
        _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("vision provider down"));

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("SLIDEBODYTEXT")
            .Image(Png(alt: null, x: 100, y: 1_000_000)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("SLIDEBODYTEXT");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("OCR");
    }

    [Fact]
    public async Task Skips_vector_images_and_marks_incomplete()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        var emf = new PptxFixtures.ImageSpec(new byte[] { 1, 2, 3, 4 }, "image/x-emf", null, 100, 1_000_000);
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(emf));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("decoded");
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renders_chart_backing_data_as_a_markdown_table()
    {
        var chart = new PptxFixtures.ChartSpec(
            Title: "Quarterly Revenue",
            Categories: new[] { "Q1", "Q2" },
            Series: new[] { ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }) });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Slide title", isTitle: true, y: 100)
            .Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Quarterly Revenue");
        result.Markdown.ShouldContain("| Category | Revenue |");
        result.Markdown.ShouldContain("| Q1 | 10 |");
        result.Markdown.ShouldContain("| Q2 | 20 |");
        result.IsComplete.ShouldBeTrue();
        // Charts are pure structured extraction — no OCR call.
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_incomplete_for_a_chart_whose_series_have_conflicting_categories()
    {
        // Two series with a DIFFERENT label at the same category index do not share one axis; a single wide
        // table would hang the second series' values under the wrong category. Rather than emit that silent
        // mismatch, the chart is dropped and the completeness signal trips (honest signal).
        var chart = new PptxFixtures.ChartSpec(
            Title: "Conflicting",
            Categories: new[] { "Q1", "Q2" },
            Series: new[]
            {
                ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }),
                ("Cost", (IReadOnlyList<string>)new[] { "3", "4" })
            },
            SecondSeriesCategories: new[] { "Spring", "Summer" });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Slide body")
            .Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Slide body");          // slide text retained
        result.Markdown.ShouldNotContain("Spring");           // the misaligned table is not emitted
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("chart");
    }

    [Fact]
    public async Task Renders_a_multi_series_chart_that_shares_one_category_axis()
    {
        // Sanity: two series sharing the SAME categories must still render a single wide table (the guard
        // must not over-fire on the normal shared-axis case).
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: new[] { "Q1", "Q2" },
            Series: new[]
            {
                ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }),
                ("Cost", (IReadOnlyList<string>)new[] { "3", "4" })
            });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("| Category | Revenue | Cost |");
        result.Markdown.ShouldContain("| Q1 | 10 | 3 |");
        result.Markdown.ShouldContain("| Q2 | 20 | 4 |");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Accepts_an_image_whose_size_is_exactly_the_byte_cap()
    {
        StubOcr("FIGURE");

        // An image of exactly N bytes must be ACCEPTED (the cap aborts on > cap, not >= cap). Guards a
        // `>` → `>=` regression of the bounded read.
        var bytes = TinyPng.CreateSolid(48, 48);
        var image = new PptxFixtures.ImageSpec(bytes, "image/png", null, 100, 1_000_000);
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Text("Body text").Image(image));

        var result = await CreateExtractor(maxImageBytes: bytes.Length).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("FIGURE");
        result.IsComplete.ShouldBeTrue();
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seeds_chart_categories_from_a_later_series_when_the_first_omits_them()
    {
        // The first series carries no category cache; categories must seed from the second series, not
        // degrade to numeric labels.
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: new[] { "Q1", "Q2" },
            Series: new[]
            {
                ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }),
                ("Cost", (IReadOnlyList<string>)new[] { "3", "4" })
            },
            FirstSeriesOmitsCategories: true);

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("| Q1 | 10 | 3 |");
        result.Markdown.ShouldContain("| Q2 | 20 | 4 |");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Unions_extra_non_conflicting_category_labels_from_a_later_series()
    {
        // Series 1 labels indices 0-1; series 2 additionally labels index 2 ("Q3") without conflicting.
        // The extra label must be UNIONed in (not dropped to the numeric "3" fallback).
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: new[] { "Q1", "Q2" },
            Series: new[]
            {
                ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }),
                ("Cost", (IReadOnlyList<string>)new[] { "3", "4", "5" })
            },
            SecondSeriesCategories: new[] { "Q1", "Q2", "Q3" });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // The idx-2 row carries the real "Q3" label (a numeric fallback would label it "3") and series-2's
        // value 5.
        result.Markdown.ShouldContain("| Q3 |  | 5 |");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Collapses_a_multi_line_chart_title_to_one_line()
    {
        var chart = new PptxFixtures.ChartSpec(
            Title: "Revenue\nby Quarter",
            Categories: new[] { "Q1" },
            Series: new[] { ("Revenue", (IReadOnlyList<string>)new[] { "10" }) });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // The newline in the title must collapse so the bold run / table header is not broken.
        result.Markdown.ShouldContain("**Revenue by Quarter**");
    }

    [Fact]
    public async Task Renders_native_table()
    {
        var table = new PptxFixtures.TableSpec(new IReadOnlyList<string>[]
        {
            new[] { "Name", "Amount" },
            new[] { "Widget", "42" }
        });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Table(table));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("| Name | Amount |");
        result.Markdown.ShouldContain("| Widget | 42 |");
    }

    [Fact]
    public async Task Renders_a_ragged_table_as_a_rectangular_markdown_table()
    {
        // First row is a single (merged-style) title cell; data rows have 3 columns. The separator and
        // every row must use the widest row's column count, or the Markdown table renders broken.
        var table = new PptxFixtures.TableSpec(new IReadOnlyList<string>[]
        {
            new[] { "Summary" },
            new[] { "A", "B", "C" },
            new[] { "1", "2", "3" }
        });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Table(table));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // Separator row must have 3 columns (the widest), not 1 (the header row's count).
        result.Markdown.ShouldContain("| --- | --- | --- |");
        // The short header row is padded out to 3 columns.
        result.Markdown.ShouldContain("| Summary |  |  |");
        result.Markdown.ShouldContain("| A | B | C |");
    }

    [Fact]
    public async Task Preserves_soft_line_breaks_without_fusing_text()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .TextWithSoftBreak("123 Main St", "Suite 400"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // The a:br must not fuse the two runs into "123 Main StSuite 400".
        result.Markdown.ShouldNotContain("StSuite");
        result.Markdown.ShouldContain("123 Main St");
        result.Markdown.ShouldContain("Suite 400");
    }

    [Fact]
    public async Task Keeps_separate_paragraphs_as_distinct_markdown_blocks()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Revenue up 10%\nCosts flat\nHiring freeze"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // Paragraphs are joined with a blank line so they don't collapse into one rendered paragraph.
        result.Markdown.ShouldContain("Revenue up 10%\n\nCosts flat");
        result.Markdown.ShouldContain("Costs flat\n\nHiring freeze");
    }

    [Fact]
    public async Task Does_not_use_an_axis_title_as_the_chart_caption()
    {
        // No chart title, but a value-axis title exists. The renderer must not promote the axis title.
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: new[] { "Q1", "Q2" },
            Series: new[] { ("Revenue", (IReadOnlyList<string>)new[] { "10", "20" }) },
            AxisTitle: "USD millions");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("| Q1 | 10 |");          // table still rendered
        result.Markdown.ShouldNotContain("**USD millions**");  // axis title not used as the chart caption
    }

    [Fact]
    public async Task Keeps_all_chart_points_when_idx_is_omitted()
    {
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: new[] { "Jan", "Feb", "Mar" },
            Series: new[] { ("Sales", (IReadOnlyList<string>)new[] { "7", "8", "9" }) },
            OmitPointIdx: true);

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // The positional fallback keeps every point aligned even though no pt carries an idx attribute.
        result.Markdown.ShouldContain("| Jan | 7 |");
        result.Markdown.ShouldContain("| Feb | 8 |");
        result.Markdown.ShouldContain("| Mar | 9 |");
    }

    [Fact]
    public async Task Skips_a_mislabeled_image_whose_bytes_are_not_a_known_raster()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // Declared image/png, but the bytes carry no PNG signature (corrupt / mislabeled part).
        var bogus = new PptxFixtures.ImageSpec(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }, "image/png", null, 100, 1_000_000);
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Text("Body text").Image(bogus));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("decoded");
        // No OCR call wasted on garbage bytes.
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renders_title_placeholder_as_heading()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Annual Report", isTitle: true, y: 100));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("## Annual Report");
    }

    [Fact]
    public async Task Excludes_slide_number_field_from_the_body_text()
    {
        // A slide-number placeholder's cached value ("7") must not leak into the body Markdown — matching
        // the exclusion the notes path already applies.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Real slide content")
            .WithSlideNumberField("7"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Real slide content");
        result.Markdown.ShouldNotContain("7");
    }

    [Fact]
    public async Task Renders_a_multi_paragraph_title_as_a_single_clean_heading()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Annual Report\n2024", isTitle: true, y: 100));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        // Paragraph breaks in a title collapse to a single space (no "## Annual Report  2024" double space).
        result.Markdown.ShouldContain("## Annual Report 2024");
        result.Markdown.ShouldNotContain("Annual Report  2024");
    }

    [Fact]
    public async Task Includes_speaker_notes_when_enabled()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Visible content")
            .WithNotes("Remember to mention the Q3 numbers"));

        var result = await CreateExtractor(includeNotes: true).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("### Speaker notes");
        result.Markdown.ShouldContain("Remember to mention the Q3 numbers");
    }

    [Fact]
    public async Task Excludes_speaker_notes_by_default()
    {
        // The production default must NOT include notes (author-private content must not silently reach the
        // egress); a host has to opt in. Construct with default options to assert the shipped default.
        var extractor = new PptxExtractor(
            _ocr,
            Options.Create(new OpenXmlExtractorOptions()));

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Visible content")
            .WithNotes("Internal-only speaker note"));

        var result = await extractor.ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Visible content");
        result.Markdown.ShouldNotContain("Speaker notes");
        result.Markdown.ShouldNotContain("Internal-only speaker note");
    }

    [Fact]
    public async Task Skips_an_oversized_image_and_marks_incomplete()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // A valid PNG whose decompressed size exceeds a tiny per-image byte cap. It must be skipped before
        // full materialization (the ZIP-decompression-bomb guard), not transcribed.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000)));

        var result = await CreateExtractor(maxImageBytes: 50).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("size cap");
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Excludes_speaker_notes_when_disabled()
    {
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Visible content")
            .WithNotes("Hidden author note"));

        var result = await CreateExtractor(includeNotes: false).ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Visible content");
        result.Markdown.ShouldNotContain("Speaker notes");
        result.Markdown.ShouldNotContain("Hidden author note");
    }

    [Fact]
    public async Task Orders_slides_in_presentation_order()
    {
        var pptx = PptxFixtures.Build(
            new PptxFixtures.SlideSpec().Text("FIRST_SLIDE"),
            new PptxFixtures.SlideSpec().Text("SECOND_SLIDE"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        var first = result.Markdown.IndexOf("FIRST_SLIDE", StringComparison.Ordinal);
        var second = result.Markdown.IndexOf("SECOND_SLIDE", StringComparison.Ordinal);
        first.ShouldBeGreaterThanOrEqualTo(0);
        second.ShouldBeGreaterThan(first);
    }

    [Fact]
    public async Task Returns_empty_and_incomplete_for_an_unopenable_file()
    {
        var notPptx = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a pptx"));

        var result = await CreateExtractor().ExtractAsync(notPptx, PptxContext());

        // No whole-page OCR fallback exists for PPTX, so an unopenable deck is reported as empty +
        // incomplete (the honest #268 signal), not a silent empty success.
        result.Markdown.ShouldBeNullOrEmpty();
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.ProviderName.ShouldBe(PptxExtractor.ProviderIdentifier);
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transcribes_images_nested_inside_a_grouped_shape()
    {
        StubOcr("GROUPED_FIGURE_TEXT");

        // The image lives inside a p:grpSp; the walker must recurse into the group to find it (#307 decision).
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Slide body")
            .ImageInGroup(Png(alt: "Nested diagram", x: 100, y: 100)));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("GROUPED_FIGURE_TEXT");
        result.Markdown.ShouldContain("**Nested diagram**");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_incomplete_when_a_chart_cannot_be_rendered()
    {
        // A chart whose series carries no cached values (e.g. scatter-style data) yields no table; it is
        // counted as lost via the completeness signal rather than silently dropped.
        var chart = new PptxFixtures.ChartSpec(
            Title: "Unsupported",
            Categories: System.Array.Empty<string>(),
            Series: new[] { ("S", (IReadOnlyList<string>)System.Array.Empty<string>()) });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Slide body")
            .Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Slide body");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("chart");
    }

    [Fact]
    public async Task Passes_empty_language_hints_to_figure_ocr_when_the_context_has_none()
    {
        StubOcr("FIGURE");

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Body text")
            .Image(Png(alt: null, x: 100, y: 1_000_000)));

        // #441: no central host default. With no per-document hints, the figure path passes empty hints;
        // a provider that needs a default reads its own config (e.g. PaddleOcr:Languages).
        var extractor = CreateExtractor();
        await extractor.ExtractAsync(new MemoryStream(pptx), PptxContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Surfaces_a_shape_wrapped_in_mc_alternate_content()
    {
        // #319: a shape PowerPoint wraps in <mc:AlternateContent> is a direct spTree child of type
        // AlternateContent — none of the typed WalkShapesAsync cases — so without collapsing the
        // markup-compatibility fork on open its text is silently dropped with no #268 signal. Opening with the
        // MC-collapsing settings resolves the fork to its selected branch (a real p:sp) before the walk runs.
        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec()
            .Text("Ordinary slide text")
            .McAlternateContentText("MC_WRAPPED_CONTENT"));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("Ordinary slide text");
        result.Markdown.ShouldContain("MC_WRAPPED_CONTENT");
        // Exactly one branch survives collapsing — the content must not be doubled from both Choice and Fallback.
        (result.Markdown.Split("MC_WRAPPED_CONTENT").Length - 1).ShouldBe(1);
    }

    [Fact]
    public async Task Renders_a_multi_level_category_axis_as_compound_row_labels()
    {
        // #321: a multi-level category axis (c:multiLvlStrRef with an inner "quarter" level and an outer
        // "year" level) repeats idx values per level. Flattening every level into one idx→label map collided
        // the indices and mislabeled rows (idx 0 became the outer "2023" instead of "2023 / Q1"). The renderer
        // must compose one compound label per position, outer → inner.
        var chart = new PptxFixtures.ChartSpec(
            Title: null,
            Categories: System.Array.Empty<string>(),
            Series: new[] { ("Revenue", (IReadOnlyList<string>)new[] { "10", "20", "30", "40" }) },
            TwoLevelCategories: new[] { ("2023", "Q1"), ("2023", "Q2"), ("2024", "Q1"), ("2024", "Q2") });

        var pptx = PptxFixtures.Build(new PptxFixtures.SlideSpec().Chart(chart));

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pptx), PptxContext());

        result.Markdown.ShouldContain("| 2023 / Q1 | 10 |");
        result.Markdown.ShouldContain("| 2023 / Q2 | 20 |");
        result.Markdown.ShouldContain("| 2024 / Q1 | 30 |");
        result.Markdown.ShouldContain("| 2024 / Q2 | 40 |");
        result.IsComplete.ShouldBeTrue();
    }
}

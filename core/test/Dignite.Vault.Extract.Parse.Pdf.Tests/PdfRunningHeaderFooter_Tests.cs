using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// #383: cross-page running header / footer + page-number stripping, exercised end-to-end through
/// <see cref="PdfExtractor"/> on multi-page digital PDFs. The pages carry no images, so the stubbed
/// <see cref="IOcrProvider"/> is never called.
/// </summary>
public class PdfRunningHeaderFooter_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private PdfExtractor CreateExtractor()
        => new(
            _ocr,
            Options.Create(new PdfExtractorOptions()),
            Options.Create(new ExtractOcrOptions { DefaultLanguageHints = new List<string>() }));

    private static TextExtractionContext PdfContext()
        => new() { ContentType = "application/pdf", FileExtension = ".pdf" };

    // A page edge band (header ~810, footer ~30) with body lines in between. PDF origin is bottom-left, so
    // a larger Y is higher on the page.
    private static IReadOnlyList<(string, double)> Page(string header, string footer, params string[] body)
    {
        var lines = new List<(string, double)> { (header, 810.0) };
        var y = 700.0;
        foreach (var line in body)
        {
            lines.Add((line, y));
            y -= 40.0;
        }

        lines.Add((footer, 30.0));
        return lines;
    }

    [Fact]
    public async Task Strips_running_header_and_page_number_footer_across_pages()
    {
        var pdf = PdfFixtures.BuildMultiPage(new[]
        {
            Page("ACME CONFIDENTIAL", "Page 1 of 3", "Section one alpha", "Detail about the alpha topic"),
            Page("ACME CONFIDENTIAL", "Page 2 of 3", "Section two beta", "Detail about the beta topic"),
            Page("ACME CONFIDENTIAL", "Page 3 of 3", "Section three gamma", "Detail about the gamma topic")
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Running header repeated at the top edge of every page → stripped.
        result.Markdown.ShouldNotContain("ACME CONFIDENTIAL");
        // Page-number footer: the digit varies per page but the normalized template "page # of #" repeats →
        // stripped on every page (this is the original #383 complaint).
        result.Markdown.ShouldNotContain("of 3");

        // Distinct body content is preserved — including lines that fall inside the edge band but do not repeat.
        result.Markdown.ShouldContain("Section one alpha");
        result.Markdown.ShouldContain("Section two beta");
        result.Markdown.ShouldContain("Section three gamma");
        result.Markdown.ShouldContain("Detail about the alpha topic");
    }

    [Fact]
    public async Task Preserves_a_non_repeating_signature_and_total_at_the_page_bottom()
    {
        // The critical safety case: a signature block and a total sit in the FOOTER band of the last page but
        // do NOT repeat across pages, so they must survive even though the genuine page-number footer beside
        // them is stripped. "Delete by position alone" would lose them — "delete only what repeats" keeps them.
        var pdf = PdfFixtures.BuildMultiPage(new[]
        {
            Page("MASTER AGREEMENT", "Page 1 of 2", "Clause one is here"),
            new List<(string, double)>
            {
                ("MASTER AGREEMENT", 810.0),
                ("Clause two is here", 700.0),
                ("Total: 9,999.00 USD", 70.0),
                ("Signed: Jane Doe", 50.0),
                ("Page 2 of 2", 30.0)
            }
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Body at the very bottom of the page is kept (the whole point of repetition-gated stripping).
        result.Markdown.ShouldContain("Signed: Jane Doe");
        result.Markdown.ShouldContain("Total: 9,999.00 USD");
        // The repeating header + page-number footer are still stripped.
        result.Markdown.ShouldNotContain("MASTER AGREEMENT");
        result.Markdown.ShouldNotContain("of 2");
        result.Markdown.ShouldContain("Clause one is here");
        result.Markdown.ShouldContain("Clause two is here");
    }

    [Fact]
    public async Task Keeps_the_footer_of_a_single_page_document()
    {
        // Single page → no cross-page repetition signal → nothing is stripped (accepted #383 limitation).
        var pdf = PdfFixtures.BuildMultiPage(new[]
        {
            Page("ACME CONFIDENTIAL", "Page 1 of 1", "The only body line")
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("ACME CONFIDENTIAL");
        result.Markdown.ShouldContain("Page 1 of 1");
        result.Markdown.ShouldContain("The only body line");
    }

    [Fact]
    public async Task Does_not_strip_a_repeated_line_outside_the_edge_band()
    {
        // A line that repeats verbatim across pages but sits in the MIDDLE of the page (outside the
        // first/last-N candidate band) is body content, not chrome — position gates candidate selection, so
        // it is never even considered for stripping.
        IReadOnlyList<(string, double)> MakePage(string suffix, string footer) => new List<(string, double)>
        {
            ("TOP HEADER", 810.0),
            ($"first unique {suffix}", 720.0),
            ($"second unique {suffix}", 690.0),
            ("REPEATED MIDDLE LINE", 640.0), // identical on both pages, but middle of the page
            ($"third unique {suffix}", 590.0),
            ($"fourth unique {suffix}", 560.0),
            (footer, 30.0)
        };

        var pdf = PdfFixtures.BuildMultiPage(new[]
        {
            MakePage("a", "Page 1 of 2"),
            MakePage("b", "Page 2 of 2")
        });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Middle repeated line is kept; edge-band running header/footer are stripped.
        result.Markdown.ShouldContain("REPEATED MIDDLE LINE");
        result.Markdown.ShouldNotContain("TOP HEADER");
        result.Markdown.ShouldNotContain("of 2");
    }
}

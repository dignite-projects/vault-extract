using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Dignite.Vault.Extract.Parse;
using Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

public class DefaultTextExtractor_Tests : AbpIntegratedTest<DefaultTextExtractor_Tests.ParseTestModule>
{
    private readonly ITextExtractor _extractor;
    private readonly IOcrProvider _ocrProvider;

    public DefaultTextExtractor_Tests()
    {
        _extractor = GetRequiredService<ITextExtractor>();
        _ocrProvider = GetRequiredService<IOcrProvider>();
    }

    [Fact]
    public async Task Should_Use_OcrProvider_For_Image_Files()
    {
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 }); // fake JPEG bytes
        var ctx = new TextExtractionContext
        {
            ContentType = "image/jpeg",
            FileExtension = ".jpg"
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        // OCR Provider directly owns Markdown output, even when it is a flat paragraph; DefaultTextExtractor
        // passes fields through.
        result.Markdown.ShouldBe("fake ocr markdown");
        result.UsedOcr.ShouldBeTrue();

        // OCR orchestration calls the provider only once; the concrete model is selected by provider/host
        // configuration.
        await _ocrProvider.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o =>
                o.ContentType == "image/jpeg" &&
                // #441: no central host default; with no per-document hints the extractor passes empty hints.
                o.LanguageHints.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Use_Markdown_Provider_For_Txt_Files()
    {
        var content = "Hello World";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var ctx = new TextExtractionContext
        {
            ContentType = "text/plain",
            FileExtension = ".txt"
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.Markdown.ShouldContain("Hello World");
        result.UsedOcr.ShouldBeFalse();
    }

    [Theory]
    [InlineData(".csv", "text/csv", "Name,City\nAlice,Tokyo", "Alice", "Tokyo")]
    [InlineData(".tsv", "text/tab-separated-values", "Name\tCity\nAlice\tTokyo", "Alice", "Tokyo")]
    public async Task Should_Convert_Delimited_Files_To_Markdown_Tables(
        string extension,
        string contentType,
        string content,
        string expectedName,
        string expectedCity)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var ctx = new TextExtractionContext
        {
            ContentType = contentType,
            FileExtension = extension
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.Markdown.ShouldContain("|");
        result.Markdown.ShouldContain(expectedName);
        result.Markdown.ShouldContain(expectedCity);
        result.UsedOcr.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Convert_All_NonEmpty_Xlsx_Worksheets_To_Markdown()
    {
        using var workbook = new XLWorkbook();
        var people = workbook.AddWorksheet("People");
        people.Cell("A1").Value = "Name";
        people.Cell("B1").Value = "City";
        people.Cell("A2").Value = "Alice";
        people.Cell("B2").Value = "Tokyo";

        var totals = workbook.AddWorksheet("Totals");
        totals.Cell("A1").Value = "Count";
        totals.Cell("A2").Value = 1;
        workbook.AddWorksheet("Empty");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var result = await _extractor.ExtractAsync(stream, new TextExtractionContext
        {
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileExtension = ".xlsx"
        });

        result.Markdown.ShouldContain("People");
        result.Markdown.ShouldContain("Alice");
        result.Markdown.ShouldContain("Tokyo");
        result.Markdown.ShouldContain("Totals");
        result.Markdown.ShouldContain("Count");
        result.UsedOcr.ShouldBeFalse();
        result.ProviderName.ShouldBe(ElBrunoMarkdownProvider.ProviderIdentifier);
    }

    [Fact]
    public async Task Should_Fail_Explicitly_For_Malformed_Xlsx()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not an xlsx package"));
        var ctx = new TextExtractionContext
        {
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileExtension = ".xlsx"
        };

        await Should.ThrowAsync<InvalidDataException>(() => _extractor.ExtractAsync(stream, ctx));
    }

    [Fact]
    public async Task Xlsx_Preflight_Should_Reject_Expanded_Content_Over_The_Budget()
    {
        using var stream = BuildZip(("xl/sharedStrings.xml", new string('x', 512)));
        var limits = TestXlsxLimits(maxExpandedArchiveBytes: 128);

        var exception = await Should.ThrowAsync<InvalidDataException>(
            () => XlsxSafetyGuard.ValidatePackageAsync(stream, limits));

        exception.Message.ShouldContain("expanded content exceeds");
    }

    [Fact]
    public async Task Xlsx_Preflight_Should_Reject_Cell_Count_Over_The_Budget()
    {
        const string worksheet =
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetData><row><c/><c/><c/></row></sheetData></worksheet>";
        using var stream = BuildZip(("xl/worksheets/sheet1.xml", worksheet));
        var limits = TestXlsxLimits(maxCells: 2);

        var exception = await Should.ThrowAsync<InvalidDataException>(
            () => XlsxSafetyGuard.ValidatePackageAsync(stream, limits));

        exception.Message.ShouldContain("more than 2 cells");
    }

    [Fact]
    public void Xlsx_Output_Should_Reject_Markdown_Over_The_Budget()
    {
        var limits = TestXlsxLimits(maxMarkdownCharacters: 4);

        var exception = Should.Throw<InvalidDataException>(
            () => XlsxSafetyGuard.ValidateMarkdown("12345", limits));

        exception.Message.ShouldContain("5 characters");
    }

    [Fact]
    public async Task Should_Preserve_Markdown_For_Md_Files()
    {
        var content = "# Title\n\nSome content.";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var ctx = new TextExtractionContext
        {
            ContentType = "text/markdown",
            FileExtension = ".md"
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.UsedOcr.ShouldBeFalse();
        result.Markdown.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("# Title");
        result.Markdown.ShouldContain("Some content");
    }

    [Fact]
    public async Task Should_Fallback_To_Ocr_For_Scanned_Pdf()
    {
        // Non-real PDF bytes make ElBruno conversion fail, so it falls back to OCR.
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf"));
        var ctx = new TextExtractionContext
        {
            ContentType = "application/pdf",
            FileExtension = ".pdf"
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.UsedOcr.ShouldBeTrue();
        result.Markdown.ShouldBe("fake ocr markdown");
    }

    [Fact]
    public async Task Should_Not_Retry_Ocr_For_Image()
    {
        _ocrProvider.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new OcrResult
            {
                Markdown = "| A | B |\n|---|---|\n| kept | table |"
            });

        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 });
        var ctx = new TextExtractionContext
        {
            ContentType = "image/jpeg",
            FileExtension = ".jpg"
        };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.Markdown.ShouldContain("kept");

        // Orchestrator does not retry OCR; provider is called only once.
        await _ocrProvider.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Any<OcrOptions>(),
            Arg.Any<CancellationToken>());
    }

    // === #210 provenance: ProviderName propagation + NativePayload flat-field mapping ===

    [Fact]
    public async Task Image_Path_Propagates_ProviderName_And_Maps_Native_Payload()
    {
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 });
        var ctx = new TextExtractionContext { ContentType = "image/jpeg", FileExtension = ".jpg" };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.ProviderName.ShouldBe("FakeOcr");

        // OCR provider native-payload flat fields are mapped by the orchestration layer to
        // TextExtractionResult.NativePayload, the Abstractions type.
        result.NativePayload.ShouldNotBeNull();
        result.NativePayload!.SchemaName.ShouldBe("FakeOcr/schema");
        result.NativePayload.ContentType.ShouldBe("application/json");
        result.NativePayload.Content.Length.ShouldBe(4);
    }

    [Fact]
    public async Task Digital_Path_Propagates_ProviderName_And_Null_Native_Payload()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Title\n\nbody text"));
        var ctx = new TextExtractionContext { ContentType = "text/markdown", FileExtension = ".md" };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.ProviderName.ShouldBe(ElBrunoMarkdownProvider.ProviderIdentifier);

        // Pure text-to-Markdown has no spatial model, so there is no native payload.
        result.NativePayload.ShouldBeNull();
    }

    [Fact]
    public async Task Scanned_Pdf_Fallback_Uses_Ocr_ProviderName_And_Native_Payload()
    {
        // Non-real PDF bytes make ElBruno conversion fail with no text layer, so it falls back to OCR.
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf"));
        var ctx = new TextExtractionContext { ContentType = "application/pdf", FileExtension = ".pdf" };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.UsedOcr.ShouldBeTrue();
        result.ProviderName.ShouldBe("FakeOcr");
        result.NativePayload.ShouldNotBeNull();
        result.NativePayload!.SchemaName.ShouldBe("FakeOcr/schema");
    }

    [DependsOn(
        typeof(VaultExtractParseModule),
        typeof(VaultExtractParseElBrunoMarkItDownModule))]
    public class ParseTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            // OCR provider owns selecting the configured deployment model and outputting Markdown;
            // orchestrator does not retry profiles.
            // Includes flat NativePayload fields plus provider identity, covering #210 provenance assembly:
            // orchestration pass-through plus NativePayload mapping.
            var fakeOcr = Substitute.For<IOcrProvider>();
            fakeOcr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
                .Returns(new OcrResult
                {
                    Markdown = "fake ocr markdown",
                    ProviderName = "FakeOcr",
                    NativePayloadContent = new byte[] { 1, 2, 3, 4 },
                    NativePayloadContentType = "application/json",
                    NativePayloadSchemaName = "FakeOcr/schema"
                });

            context.Services.AddSingleton<IOcrProvider>(fakeOcr);
        }
    }

    private static MemoryStream BuildZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(
                    entry.Open(), Encoding.UTF8, bufferSize: 1_024, leaveOpen: false);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static XlsxSafetyLimits TestXlsxLimits(
        long maxExpandedArchiveBytes = 10_000,
        long maxCells = 100,
        int maxMarkdownCharacters = 10_000)
        => new(
            MaxCompressedBytes: 10_000,
            MaxExpandedArchiveBytes: maxExpandedArchiveBytes,
            MaxArchiveEntries: 10,
            MaxWorksheets: 5,
            MaxCells: maxCells,
            MaxMarkdownCharacters: maxMarkdownCharacters);
}

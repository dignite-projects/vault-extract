using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Dignite.Vault.Extract.Parse;
using Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Verifies DefaultTextExtractor's per-extension Markdown-provider dispatch: a specialized provider that
/// claims <c>.pdf</c> at a higher priority wins over the ElBruno catch-all, while other extensions still
/// fall through to the catch-all. This is the dispatch model behind the PdfExtractor module — and the
/// regression guard that omitting a specialized module degrades gracefully to ElBruno.
/// </summary>
public class MarkdownProviderDispatch_Tests : AbpIntegratedTest<MarkdownProviderDispatch_Tests.DispatchTestModule>
{
    public const string FakePdfMarkdown = "FAKE_PDF_PROVIDER_OUTPUT";

    private readonly ITextExtractor _extractor;

    public MarkdownProviderDispatch_Tests()
    {
        _extractor = GetRequiredService<ITextExtractor>();
    }

    // Run under Autofac, the production host's container, so provider resolution / IEnumerable injection
    // matches production rather than the default Microsoft DI container.
    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    [Fact]
    public async Task Dispatches_pdf_to_the_specialized_provider_over_the_catch_all()
    {
        var ctx = new TextExtractionContext { ContentType = "application/pdf", FileExtension = ".pdf" };

        var result = await _extractor.ExtractAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 placeholder")), ctx);

        // The higher-priority .pdf provider wins; ElBruno (catch-all) is not used and no OCR fallback runs.
        result.Markdown.ShouldBe(FakePdfMarkdown);
    }

    [Fact]
    public async Task Dispatches_other_extensions_to_the_catch_all_provider()
    {
        var ctx = new TextExtractionContext { ContentType = "text/plain", FileExtension = ".txt" };

        var result = await _extractor.ExtractAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("hello dispatch")), ctx);

        result.Markdown.ShouldContain("hello dispatch");
        result.Markdown.ShouldNotContain(FakePdfMarkdown);
    }

    [DependsOn(
        typeof(ExtractParseModule),
        typeof(ExtractParseElBrunoMarkItDownModule))]
    public class DispatchTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var fakeOcr = Substitute.For<IOcrProvider>();
            fakeOcr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
                .Returns(new OcrResult { Markdown = "fake ocr markdown", ProviderName = "FakeOcr" });
            context.Services.AddSingleton(fakeOcr);

            // A specialized provider that owns ".pdf" at the standard specialized priority (> fallback).
            var fakePdf = Substitute.For<IMarkdownTextProvider>();
            fakePdf.CanHandle(".pdf").Returns(true);
            fakePdf.Priority.Returns(MarkdownProviderPriorities.Specialized);
            fakePdf.ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>())
                .Returns(new TextExtractionResult { Markdown = FakePdfMarkdown, ProviderName = "FakePdf" });
            context.Services.AddSingleton(fakePdf);
        }
    }
}

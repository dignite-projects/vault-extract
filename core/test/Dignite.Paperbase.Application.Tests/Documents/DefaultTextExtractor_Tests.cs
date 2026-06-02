using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.Ocr;
using Dignite.Paperbase.TextExtraction;
using Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Paperbase.Documents;

public class DefaultTextExtractor_Tests : AbpIntegratedTest<DefaultTextExtractor_Tests.TextExtractionTestModule>
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

        // OCR Provider 直接负责输出 Markdown（即便是扁平段落），DefaultTextExtractor 透传字段。
        result.Markdown.ShouldBe("fake ocr markdown");
        result.UsedOcr.ShouldBeTrue();

        // OCR 编排只调用 provider 一次；具体模型由 provider/host 配置决定。
        await _ocrProvider.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o =>
                o.ContentType == "image/jpeg" &&
                o.LanguageHints.Contains("ja") &&
                o.LanguageHints.Contains("en")),
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
        // 非真实 PDF 字节 → ElBruno 转换失败 → 回退 OCR
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

        // orchestrator 不做 OCR 重试——provider 只调用一次。
        await _ocrProvider.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Any<OcrOptions>(),
            Arg.Any<CancellationToken>());
    }

    // === #210 provenance：ProviderName 传播 + NativePayload 扁平字段映射 ===

    [Fact]
    public async Task Image_Path_Propagates_ProviderName_And_Maps_Native_Payload()
    {
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 });
        var ctx = new TextExtractionContext { ContentType = "image/jpeg", FileExtension = ".jpg" };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.ProviderName.ShouldBe("FakeOcr");

        // OCR provider 的原生 payload 扁平字段经编排层映射到 TextExtractionResult.NativePayload（Abstractions 类型）。
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

        // 纯 text→Markdown 无空间模型 → 无原生 payload。
        result.NativePayload.ShouldBeNull();
    }

    [Fact]
    public async Task Scanned_Pdf_Fallback_Uses_Ocr_ProviderName_And_Native_Payload()
    {
        // 非真实 PDF 字节 → ElBruno 转换失败（无文本层）→ 回退 OCR。
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf"));
        var ctx = new TextExtractionContext { ContentType = "application/pdf", FileExtension = ".pdf" };

        var result = await _extractor.ExtractAsync(stream, ctx);

        result.UsedOcr.ShouldBeTrue();
        result.ProviderName.ShouldBe("FakeOcr");
        result.NativePayload.ShouldNotBeNull();
        result.NativePayload!.SchemaName.ShouldBe("FakeOcr/schema");
    }

    [DependsOn(
        typeof(PaperbaseTextExtractionModule),
        typeof(PaperbaseTextExtractionElBrunoMarkItDownModule))]
    public class TextExtractionTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            // OCR provider 负责选择部署配置中的模型并输出 Markdown；orchestrator 不做 profile 重试。
            // 带扁平 NativePayload 字段 + provider 身份，覆盖 #210 provenance 组装（编排层透传 + 映射 NativePayload）。
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
}

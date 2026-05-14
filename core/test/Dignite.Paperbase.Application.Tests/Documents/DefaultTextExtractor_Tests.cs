using System.IO;
using System.Text;
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

    public DefaultTextExtractor_Tests()
    {
        _extractor = GetRequiredService<ITextExtractor>();
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
        result.Confidence.ShouldBe(0.95);
        result.UsedOcr.ShouldBeTrue();
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
        result.Confidence.ShouldBe(1.0);
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
        // Markdown 字段保留了原始结构（含 # 标题）
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

    [DependsOn(
        typeof(PaperbaseTextExtractionModule),
        typeof(PaperbaseTextExtractionElBrunoMarkItDownModule))]
    public class TextExtractionTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var fakeOcr = Substitute.For<IOcrProvider>();
            fakeOcr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>())
                .Returns(new OcrResult
                {
                    Markdown = "fake ocr markdown",
                    Confidence = 0.95,
                    PageCount = 1
                });

            context.Services.AddSingleton(fakeOcr);
        }
    }
}

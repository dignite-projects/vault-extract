using System.IO;
using System.Text;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Shouldly;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #493: byte-stream text uploads (.csv / .tsv / .txt) carry no in-band encoding declaration. ElBruno's
/// converters read them as UTF-8 with BOM sniffing only, so a CP932 CSV — what Excel on Japanese Windows
/// exports — decoded to a wall of U+FFFD. These cover the transcode shim and guard the Unicode paths that
/// already worked against regression.
/// </summary>
public class TextEncodingNormalization_Tests
    : AbpIntegratedTest<DefaultTextExtractor_Tests.ParseTestModule>
{
    private const string ReplacementChar = "�";
    private const string ByteOrderMark = "﻿";

    private readonly ITextExtractor _extractor;

    public TextEncodingNormalization_Tests()
    {
        _extractor = GetRequiredService<ITextExtractor>();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData(".csv", "text/csv", "氏名,部署\n田中太郎,営業部")]
    [InlineData(".tsv", "text/tab-separated-values", "氏名\t部署\n田中太郎\t営業部")]
    [InlineData(".txt", "text/plain", "田中太郎は営業部に所属しています。")]
    public async Task Should_Decode_Shift_Jis_Without_Bom(string extension, string contentType, string content)
    {
        var bytes = Encoding.GetEncoding(932).GetBytes(content);

        var markdown = await ExtractMarkdownAsync(bytes, extension, contentType);

        markdown.ShouldNotContain(ReplacementChar);
        markdown.ShouldContain("田中太郎");
        markdown.ShouldContain("営業部");
    }

    [Fact]
    public async Task Should_Decode_Gb18030_Without_Bom()
    {
        // The detector, not the CP932 fallback, has to own this one — a Chinese CSV decoded as Shift-JIS
        // would produce plausible-looking but wrong kana rather than U+FFFD.
        var content = "姓名,部门\n张三,销售部门\n李四,财务部门\n王五,技术部门";
        var bytes = Encoding.GetEncoding(936).GetBytes(content);

        var markdown = await ExtractMarkdownAsync(bytes, ".csv", "text/csv");

        markdown.ShouldNotContain(ReplacementChar);
        markdown.ShouldContain("张三");
        markdown.ShouldContain("销售部门");
    }

    [Theory]
    [InlineData(".csv", "text/csv")]
    [InlineData(".tsv", "text/tab-separated-values")]
    [InlineData(".txt", "text/plain")]
    public async Task Should_Not_Regress_Utf8_Without_Bom(string extension, string contentType)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("氏名\n田中太郎");

        var markdown = await ExtractMarkdownAsync(bytes, extension, contentType);

        markdown.ShouldNotContain(ReplacementChar);
        markdown.ShouldContain("田中太郎");
    }

    [Fact]
    public async Task Should_Not_Regress_Utf8_With_Bom()
    {
        var bytes = Preamble(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), "氏名,部署\n田中太郎,営業部");

        var markdown = await ExtractMarkdownAsync(bytes, ".csv", "text/csv");

        markdown.ShouldNotContain(ReplacementChar);
        markdown.ShouldContain("田中太郎");
        // The BOM must not survive into the first table cell.
        markdown.ShouldNotContain(ByteOrderMark);
    }

    [Fact]
    public async Task Should_Not_Regress_Utf16_With_Bom()
    {
        var bytes = Preamble(Encoding.Unicode, "氏名,部署\n田中太郎,営業部");

        var markdown = await ExtractMarkdownAsync(bytes, ".csv", "text/csv");

        markdown.ShouldNotContain(ReplacementChar);
        markdown.ShouldContain("田中太郎");
    }

    [Fact]
    public async Task Should_Not_Fail_On_Undeterminable_Encoding()
    {
        // Bytes that are neither UTF-8 nor confidently any legacy codepage: the parse run must still
        // complete through the CP932 fallback rather than throw, because a failed run is retried by the
        // background job and would never succeed.
        var bytes = new byte[] { 0x81, 0x40, 0xFF, 0x2C, 0x9F, 0x0A, 0xE0, 0xA1 };

        var markdown = await ExtractMarkdownAsync(bytes, ".csv", "text/csv");

        markdown.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_Handle_Empty_File()
    {
        var markdown = await ExtractMarkdownAsync([], ".csv", "text/csv");

        markdown.ShouldBeEmpty();
    }

    private async Task<string> ExtractMarkdownAsync(byte[] bytes, string extension, string contentType)
    {
        using var stream = new MemoryStream(bytes);
        var result = await _extractor.ExtractAsync(stream, new TextExtractionContext
        {
            ContentType = contentType,
            FileExtension = extension
        });

        result.UsedOcr.ShouldBeFalse();
        return result.Markdown ?? string.Empty;
    }

    private static byte[] Preamble(Encoding encoding, string content)
        => [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
}

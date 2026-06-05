using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Dignite.Paperbase.Ocr;

namespace Dignite.Paperbase.Ocr.VisionLlm;

public class VisionLlmOcrProviderTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IPdfRasterizer _rasterizer = Substitute.For<IPdfRasterizer>();
    private readonly VisionLlmOcrOptions _options = new();

    private VisionLlmOcrProvider CreateProvider()
        => new(_chatClient, _rasterizer, Options.Create(_options));

    // Magic-byte prefixes so content-type-less detection also works; content type drives most tests.
    private static byte[] FakeJpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    private static byte[] FakePdf() => new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }; // "%PDF-1"

    private void SetupSingleResponse(string text, ChatFinishReason? finishReason = null)
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = finishReason });
    }

    [Fact]
    public async Task Should_Return_Transcribed_Markdown_For_An_Image()
    {
        SetupSingleResponse("# Receipt\n\n| Item | Price |\n| --- | --- |\n| Tea | 100 |");

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakeJpeg()),
            new OcrOptions { ContentType = "image/jpeg" });

        result.Markdown.ShouldContain("Receipt");
        result.ProviderName.ShouldBe("VisionLlm");
        // Markdown-first: a chat vision LLM has no out-of-band spatial model → native payload stays null.
        result.NativePayloadContent.ShouldBeNull();
        result.NativePayloadContentType.ShouldBeNull();
        result.NativePayloadSchemaName.ShouldBeNull();
        result.DetectedLanguage.ShouldBeNull();
        // #268: a full transcription is complete.
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Send_Image_As_DataContent_With_MediaType_MaxOutputTokens_And_Temperature()
    {
        _options.MaxOutputTokens = 1234;
        _options.Temperature = 0.25f;
        // Snapshot the scalar values AT CALL TIME (not a reference to ChatOptions) so the assertion proves
        // what was sent, and cannot be fooled by any later mutation of the options instance.
        int? capturedMaxTokens = null;
        float? capturedTemperature = null;
        List<ChatMessage>? capturedMessages = null;
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedMessages = ((IEnumerable<ChatMessage>)ci[0]).ToList();
                var options = (ChatOptions?)ci[1];
                capturedMaxTokens = options?.MaxOutputTokens;
                capturedTemperature = options?.Temperature;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        await CreateProvider().RecognizeAsync(
            new MemoryStream(FakeJpeg()),
            new OcrOptions { ContentType = "image/jpeg" });

        capturedMaxTokens.ShouldBe(1234);
        capturedTemperature.ShouldBe(0.25f);

        var dataContent = capturedMessages!
            .SelectMany(m => m.Contents)
            .OfType<DataContent>()
            .ShouldHaveSingleItem();
        dataContent.MediaType.ShouldBe("image/jpeg");

        // Instructions ride a System-role message (compile-time constant, not user-derived).
        capturedMessages!.ShouldContain(m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task Should_Keep_Text_When_FinishReason_Is_Length()
    {
        // Hitting the token cap without a detected loop is treated as a possibly-truncated dense page:
        // the text is KEPT (with a warning), not discarded.
        SetupSingleResponse("dense transcription that hit the cap", ChatFinishReason.Length);

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakeJpeg()),
            new OcrOptions { ContentType = "image/jpeg" });

        result.Markdown.ShouldBe("dense transcription that hit the cap");
        // #268: truncated at the token cap → kept but flagged incomplete.
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("truncated");
    }

    [Fact]
    public async Task Should_Return_Empty_And_Not_Call_Llm_For_Unsupported_Input()
    {
        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04 }), // ZIP magic — neither image nor PDF
            new OcrOptions { ContentType = "application/zip" });

        result.Markdown.ShouldBeEmpty();
        // #268: an input this provider cannot handle is flagged incomplete (not a silent blank success).
        result.IsComplete.ShouldBeFalse();
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Discard_Output_That_Trips_The_Repetition_Guard()
    {
        var loop = string.Join("\n", Enumerable.Repeat("¥980 ポイント", 50));
        SetupSingleResponse(loop);

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakeJpeg()),
            new OcrOptions { ContentType = "image/jpeg" });

        result.Markdown.ShouldBeEmpty();
        // #268: a discarded loop is flagged incomplete (the content was unusable, not genuinely blank).
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Transcribe_Each_Pdf_Page_And_Join_Them()
    {
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(3);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page A")),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page B")),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page C")));

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakePdf()),
            new OcrOptions { ContentType = "application/pdf" });

        result.Markdown.ShouldBe("page A\n\npage B\n\npage C");
        // #268: every page transcribed → complete.
        result.IsComplete.ShouldBeTrue();
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Skip_A_Bad_Pdf_Page_But_Keep_The_Rest()
    {
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(3);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var loop = string.Join("\n", Enumerable.Repeat("dup", 50));
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page A")),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, loop)),   // tripped → dropped
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page C")));

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakePdf()),
            new OcrOptions { ContentType = "application/pdf" });

        result.Markdown.ShouldBe("page A\n\npage C");
        // #268: a dropped page → the document is flagged incomplete.
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Throw_When_Pdf_Exceeds_MaxPdfPages()
    {
        _options.MaxPdfPages = 2;
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(5);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            CreateProvider().RecognizeAsync(
                new MemoryStream(FakePdf()),
                new OcrOptions { ContentType = "application/pdf" }));

        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Skip_A_Pdf_Page_That_Fails_To_Rasterize_But_Keep_The_Rest()
    {
        // A single page PDFium cannot render must not discard the other (already paid-for) pages.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(3);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(png);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Is(1))
            .Returns<byte[]>(_ => throw new InvalidOperationException("corrupt page object"));
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page A")),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "page C")));

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakePdf()),
            new OcrOptions { ContentType = "application/pdf" });

        result.Markdown.ShouldBe("page A\n\npage C");
        // #268: a dropped page → the document is flagged incomplete.
        result.IsComplete.ShouldBeFalse();
        // page 1 (index 1) failed to rasterize → skipped before the LLM call, so only 2 LLM calls.
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Detect_Pdf_By_Magic_Bytes_When_ContentType_Missing()
    {
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(1);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        SetupSingleResponse("scanned page text");

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakePdf()),
            new OcrOptions { ContentType = "" });

        result.Markdown.ShouldBe("scanned page text");
        _rasterizer.Received(1).GetPageCount(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Should_Treat_Pdf_Mislabeled_As_Image_By_Magic_Bytes_Not_ContentType()
    {
        // Content-Type is a hint, not a trust boundary: a PDF carrying a wrong "image/png" Content-Type
        // must still take the PDF rasterization path (proved by GetPageCount being hit), never be sent
        // to the vision model as a single image.
        _rasterizer.GetPageCount(Arg.Any<byte[]>()).Returns(1);
        _rasterizer.RenderPageToPng(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        SetupSingleResponse("scanned text");

        var result = await CreateProvider().RecognizeAsync(
            new MemoryStream(FakePdf()),
            new OcrOptions { ContentType = "image/png" });

        result.Markdown.ShouldBe("scanned text");
        _rasterizer.Received(1).GetPageCount(Arg.Any<byte[]>());
    }
}

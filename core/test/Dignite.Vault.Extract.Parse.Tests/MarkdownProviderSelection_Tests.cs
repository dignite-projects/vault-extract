using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Unit tests for <see cref="DefaultTextExtractor"/>'s per-file Markdown-provider selection: among the
/// coexisting providers that <c>CanHandle</c> an extension, the highest <c>Priority</c> wins; ties keep the
/// incumbent deterministically; and when nothing can handle the extension the orchestrator fails loudly.
/// Selection is observed through <see cref="DefaultTextExtractor.ExtractAsync"/> by giving each provider a
/// distinct output and asserting which one was invoked.
/// </summary>
public class MarkdownProviderSelection_Tests
{
    private static TextExtractionContext DocxContext()
        => new() { FileExtension = ".docx", ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document" };

    private static MemoryStream SomeBytes() => new(Encoding.UTF8.GetBytes("payload"));

    [Fact]
    public async Task Should_Select_The_Highest_Priority_Provider_That_Can_Handle()
    {
        var high = TestDoubles.MarkdownProvider(10, ".docx", new TextExtractionResult { Markdown = "HIGH" });
        var low = TestDoubles.MarkdownProvider(1, ".docx", new TextExtractionResult { Markdown = "LOW" });
        var sut = TestDoubles.Extractor(TestDoubles.OcrReturning(), new[] { low, high });

        var result = await sut.ExtractAsync(SomeBytes(), DocxContext());

        result.Markdown.ShouldBe("HIGH");
        await low.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Ignore_A_Higher_Priority_Provider_That_Cannot_Handle()
    {
        // Priority only breaks ties among providers that CanHandle; a non-matching provider is skipped
        // regardless of how high its priority is.
        var higherButUnrelated = TestDoubles.MarkdownProvider(99, ".pdf", new TextExtractionResult { Markdown = "PDF" });
        var matching = TestDoubles.MarkdownProvider(1, ".docx", new TextExtractionResult { Markdown = "DOCX" });
        var sut = TestDoubles.Extractor(TestDoubles.OcrReturning(), new[] { higherButUnrelated, matching });

        var result = await sut.ExtractAsync(SomeBytes(), DocxContext());

        result.Markdown.ShouldBe("DOCX");
        await higherButUnrelated.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Keep_The_Incumbent_When_Two_Providers_Tie_On_Priority()
    {
        // A same-extension, same-priority clash is a config mistake; selection must still be deterministic —
        // the first-registered provider (the incumbent) is kept.
        var first = TestDoubles.MarkdownProvider(5, ".docx", new TextExtractionResult { Markdown = "FIRST" });
        var second = TestDoubles.MarkdownProvider(5, ".docx", new TextExtractionResult { Markdown = "SECOND" });
        var sut = TestDoubles.Extractor(TestDoubles.OcrReturning(), new[] { first, second });

        var result = await sut.ExtractAsync(SomeBytes(), DocxContext());

        result.Markdown.ShouldBe("FIRST");
        await second.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Throw_When_No_Provider_Can_Handle_The_Extension()
    {
        // The production host always installs the ElBruno catch-all, so this is an impossible state there —
        // which is exactly why the integration tests cannot cover it. With no capable provider the
        // orchestrator throws a module-composition error rather than failing the document silently.
        var unrelated = TestDoubles.MarkdownProvider(0, ".pdf");
        var sut = TestDoubles.Extractor(TestDoubles.OcrReturning(), new[] { unrelated });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.ExtractAsync(SomeBytes(), DocxContext()));

        ex.Message.ShouldContain(".docx");
        ex.Message.ShouldContain(nameof(IMarkdownTextProvider));
    }
}

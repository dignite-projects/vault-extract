using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Shared NSubstitute builders for the orchestrator unit tests. Keeps each test focused on the single
/// branch it exercises rather than on substitute plumbing.
/// </summary>
internal static class TestDoubles
{
    /// <summary>An <see cref="IOcrProvider"/> that returns <paramref name="result"/> for any input.</summary>
    public static IOcrProvider OcrReturning(OcrResult result)
    {
        var ocr = Substitute.For<IOcrProvider>();
        ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return ocr;
    }

    /// <summary>An <see cref="IOcrProvider"/> returning a trivial Markdown result; use when OCR is not the focus.</summary>
    public static IOcrProvider OcrReturning(string markdown = "ocr markdown")
        => OcrReturning(new OcrResult { Markdown = markdown, ProviderName = "FakeOcr" });

    /// <summary>
    /// An <see cref="IMarkdownTextProvider"/> that <see cref="IMarkdownTextProvider.CanHandle"/>s exactly
    /// <paramref name="handles"/> (case-insensitive) at <paramref name="priority"/>, returning
    /// <paramref name="result"/> (default: a small Markdown payload).
    /// </summary>
    public static IMarkdownTextProvider MarkdownProvider(
        int priority,
        string handles,
        TextExtractionResult? result = null)
    {
        var provider = Substitute.For<IMarkdownTextProvider>();
        provider.Priority.Returns(priority);
        provider.CanHandle(Arg.Any<string>())
            .Returns(ci => string.Equals((string)ci[0], handles, StringComparison.OrdinalIgnoreCase));
        provider.ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>())
            .Returns(result ?? new TextExtractionResult { Markdown = "digital markdown", ProviderName = "FakeMarkdown" });
        return provider;
    }

    public static DefaultTextExtractor Extractor(
        IOcrProvider ocr,
        IEnumerable<IMarkdownTextProvider> markdownProviders)
        => new(ocr, markdownProviders);

    public static MemoryStream Bytes(params byte[] content) => new(content);
}

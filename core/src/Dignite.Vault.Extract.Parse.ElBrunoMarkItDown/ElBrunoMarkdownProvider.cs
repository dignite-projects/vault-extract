using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Parse;
using ElBruno.MarkItDotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;

[ExposeServices(typeof(IMarkdownTextProvider))]
public class ElBrunoMarkdownProvider : IMarkdownTextProvider, ITransientDependency
{
    public const string ProviderIdentifier = "ElBruno.MarkItDotNet";

    private readonly MarkdownService _markdownService;

    public ILogger<ElBrunoMarkdownProvider> Logger { get; set; } = NullLogger<ElBrunoMarkdownProvider>.Instance;

    public ElBrunoMarkdownProvider(MarkdownService markdownService)
    {
        _markdownService = markdownService;
    }

    /// <summary>Catch-all fallback: handles every extension so any unclaimed format degrades here.</summary>
    public virtual bool CanHandle(string fileExtension) => true;

    /// <inheritdoc/>
    public virtual int Priority => MarkdownProviderPriorities.Fallback;

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        var isXlsx = string.Equals(context.FileExtension, ".xlsx", StringComparison.OrdinalIgnoreCase);
        if (isXlsx)
        {
            await XlsxSafetyGuard.ValidatePackageAsync(fileStream, cancellationToken: cancellationToken);
        }

        // ElBruno's text converters decode a raw byte stream as UTF-8 with BOM sniffing only, so a
        // legacy-encoded CSV/TSV/TXT would land in Document.Markdown as U+FFFD (#493). Hand them UTF-8.
        await using var normalized = TextEncodingNormalizer.AppliesTo(context.FileExtension)
            ? await TextEncodingNormalizer.ToUtf8Async(fileStream, context.FileExtension, Logger, cancellationToken)
            : (Stream?)null;

        var conversion = await _markdownService.ConvertAsync(
            normalized ?? fileStream,
            context.FileExtension ?? string.Empty,
            cancellationToken);

        if (!conversion.Success)
        {
            Logger.LogWarning("ElBruno conversion failed for {Extension}: {Error}",
                context.FileExtension, conversion.ErrorMessage);

            // XLSX is an explicitly supported upload format (#471), not an opportunistic catch-all format.
            // A corrupt/encrypted workbook must fail the parse run instead of being persisted as a successful
            // document with empty Markdown. PDF keeps its empty-result behavior because the orchestrator uses
            // that signal to invoke whole-page OCR for scanned PDFs.
            if (isXlsx)
            {
                throw new InvalidDataException(
                    $"Could not convert XLSX to Markdown: {conversion.ErrorMessage ?? "unknown conversion error"}");
            }

            // Still report provider identity so failed results keep provenance.
            return new TextExtractionResult { ProviderName = ProviderIdentifier };
        }

        if (isXlsx)
        {
            XlsxSafetyGuard.ValidateMarkdown(conversion.Markdown ?? string.Empty);
        }

        // Pure text-to-Markdown conversion has no spatial model, so NativePayload stays null.
        return new TextExtractionResult
        {
            Markdown = conversion.Markdown ?? string.Empty,
            DetectedLanguage = null,
            UsedOcr = false,
            ProviderName = ProviderIdentifier,
            NativePayload = null
        };
    }
}

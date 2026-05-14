using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.TextExtraction;
using ElBruno.MarkItDotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown;

[ExposeServices(typeof(IMarkdownTextProvider))]
public class ElBrunoMarkdownProvider : IMarkdownTextProvider, ITransientDependency
{
    private readonly MarkdownService _markdownService;

    public ILogger<ElBrunoMarkdownProvider> Logger { get; set; } = NullLogger<ElBrunoMarkdownProvider>.Instance;

    public ElBrunoMarkdownProvider(MarkdownService markdownService)
    {
        _markdownService = markdownService;
    }

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        var conversion = await _markdownService.ConvertAsync(
            fileStream,
            context.FileExtension ?? string.Empty,
            cancellationToken);

        if (!conversion.Success)
        {
            Logger.LogDebug("ElBruno conversion failed for {Extension}: {Error}",
                context.FileExtension, conversion.ErrorMessage);
            return new TextExtractionResult();
        }

        return new TextExtractionResult
        {
            Markdown = conversion.Markdown ?? string.Empty,
            Confidence = 1.0,
            PageCount = conversion.Metadata?.PageCount ?? 0,
            DetectedLanguage = null,
            UsedOcr = false,
        };
    }
}

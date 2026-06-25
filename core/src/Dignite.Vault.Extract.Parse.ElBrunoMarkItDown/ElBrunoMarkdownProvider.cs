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
        var conversion = await _markdownService.ConvertAsync(
            fileStream,
            context.FileExtension ?? string.Empty,
            cancellationToken);

        if (!conversion.Success)
        {
            Logger.LogDebug("ElBruno conversion failed for {Extension}: {Error}",
                context.FileExtension, conversion.ErrorMessage);
            // Still report provider identity so failed results keep provenance.
            return new TextExtractionResult { ProviderName = ProviderIdentifier };
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

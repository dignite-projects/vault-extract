using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Registers the PdfPig-based <see cref="PdfExtractor"/> as an <c>IMarkdownTextProvider</c> that claims
/// the <c>.pdf</c> extension. Markdown providers coexist and are dispatched per file by extension, so a
/// host that includes this module routes <c>.pdf</c> to <see cref="PdfExtractor"/>; a host that omits it
/// falls back to the catch-all ElBruno provider (prior behavior preserved).
/// <para>
/// Depends only on the text-extraction orchestrator module, which transitively brings the OCR contract
/// (<c>IOcrProvider</c>) consumed for embedded-image transcription. The concrete OCR provider is chosen
/// by the host (VisionLlm / PaddleOCR / Azure DI).
/// </para>
/// </summary>
[DependsOn(typeof(ExtractParseModule))]
public class ExtractParsePdfModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Bind the cost/noise caps from the "PdfExtractor" configuration section, matching the
        // appsettings-driven convention of the OCR provider modules (VisionLlmOcr / PaddleOcr / Azure).
        // Absent the section, PdfExtractorOptions keeps its defaults. PdfExtractor itself self-registers
        // via ITransientDependency + [ExposeServices(typeof(IMarkdownTextProvider))].
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<PdfExtractorOptions>(configuration.GetSection("PdfExtractor"));
    }
}

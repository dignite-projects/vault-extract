using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Registers the OpenXML-based Markdown providers: <see cref="PptxExtractor"/> (claims <c>.pptx</c>, #307)
/// and <see cref="DocxExtractor"/> (claims <c>.docx</c>, #308). Markdown providers coexist and are
/// dispatched per file by extension, so a host that includes this module routes <c>.pptx</c> /
/// <c>.docx</c> to the respective provider. <b>This module is required for <c>.pptx</c></b>: the catch-all
/// ElBruno provider does not support PresentationML, so omitting this module makes <c>.pptx</c> fall
/// through to ElBruno and yield empty Markdown (and, being non-<c>.pdf</c>, it gets no whole-page OCR
/// fallback either) — there was no prior <c>.pptx</c> capability to preserve. For <c>.docx</c>, ElBruno
/// <i>can</i> convert the file, so omitting this module degrades <c>.docx</c> gracefully to ElBruno's
/// prior output (a genuine module-composition fallback).
/// <para>
/// Depends only on the text-extraction orchestrator module, which transitively brings the OCR contract
/// (<c>IOcrProvider</c>) consumed for embedded-image transcription. The concrete OCR provider is chosen
/// by the host (VisionLlm / PaddleOCR / Azure DI).
/// </para>
/// </summary>
[DependsOn(typeof(ExtractParseModule))]
public class ExtractParseOpenXmlModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Bind the cost/noise caps + notes flag from the "OpenXmlExtractor" configuration section,
        // matching the appsettings-driven convention of the OCR / Pdf provider modules. Absent the
        // section, OpenXmlExtractorOptions keeps its defaults. PptxExtractor itself self-registers via
        // ITransientDependency + [ExposeServices(typeof(IMarkdownTextProvider))].
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<OpenXmlExtractorOptions>(configuration.GetSection("OpenXmlExtractor"));
    }
}

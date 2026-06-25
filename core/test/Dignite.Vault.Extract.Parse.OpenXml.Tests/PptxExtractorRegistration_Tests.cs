using System.Collections.Generic;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Guards the ABP conventional-registration metadata and the extension self-declaration for
/// <see cref="PptxExtractor"/>. The behavior tests construct the extractor with <c>new(...)</c>, so they
/// cannot catch a missing DI registration. <c>PptxExtractor</c> does not end with <c>MarkdownTextProvider</c>,
/// so <see cref="IMarkdownTextProvider"/> is exposed only via the explicit <c>[ExposeServices]</c> — this
/// asserts it stays exposed (otherwise DefaultTextExtractor would never see it for <c>.pptx</c> dispatch),
/// and that it claims only <c>.pptx</c> (so other extensions are left to their own providers / the ElBruno catch-all).
/// </summary>
public class PptxExtractorRegistration_Tests
{
    [Fact]
    public void Provider_Exposes_IMarkdownTextProvider()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PptxExtractor))
            .ShouldContain(typeof(IMarkdownTextProvider));
    }

    [Fact]
    public void Provider_Is_Conventionally_Registered_Via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(PptxExtractor)).ShouldBeTrue();
    }

    [Fact]
    public void Claims_only_the_pptx_extension_and_outranks_the_fallback()
    {
        var extractor = new PptxExtractor(
            Substitute.For<IOcrProvider>(),
            Options.Create(new OpenXmlExtractorOptions()),
            Options.Create(new ExtractOcrOptions { DefaultLanguageHints = new List<string>() }));

        extractor.CanHandle(".pptx").ShouldBeTrue();
        extractor.CanHandle(".PPTX").ShouldBeTrue();
        extractor.CanHandle(".pdf").ShouldBeFalse();
        extractor.CanHandle(".docx").ShouldBeFalse();
        extractor.CanHandle(".ppt").ShouldBeFalse();
        extractor.CanHandle(string.Empty).ShouldBeFalse();

        extractor.Priority.ShouldBeGreaterThan(MarkdownProviderPriorities.Fallback);
    }
}

using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Guards the ABP conventional-registration metadata and the extension self-declaration for
/// <see cref="PdfExtractor"/>. The behavior tests construct the extractor with <c>new(...)</c>, so they
/// cannot catch a missing DI registration. <c>PdfExtractor</c> does not end with <c>MarkdownTextProvider</c>,
/// so <see cref="IMarkdownTextProvider"/> is exposed only via the explicit <c>[ExposeServices]</c> — this
/// asserts it stays exposed (otherwise DefaultTextExtractor would never see it for <c>.pdf</c> dispatch).
/// </summary>
public class PdfExtractorRegistration_Tests
{
    [Fact]
    public void Provider_Exposes_IMarkdownTextProvider()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PdfExtractor))
            .ShouldContain(typeof(IMarkdownTextProvider));
    }

    [Fact]
    public void Provider_Is_Conventionally_Registered_Via_ITransientDependency()
    {
        // ABP derives the lifetime (and hence registration) from the ITransientDependency marker, NOT from
        // [ExposeServices]. GetExposedServices above only checks the attribute, so without this a dropped
        // marker would silently de-register PdfExtractor — DefaultTextExtractor would never see it and .pdf
        // would fall back to ElBruno with no embedded-image OCR — while the attribute test stayed green.
        typeof(ITransientDependency).IsAssignableFrom(typeof(PdfExtractor)).ShouldBeTrue();
    }

    [Fact]
    public void Claims_only_the_pdf_extension_and_outranks_the_fallback()
    {
        var extractor = new PdfExtractor(
            Substitute.For<IOcrProvider>(),
            Options.Create(new PdfExtractorOptions()));

        extractor.CanHandle(".pdf").ShouldBeTrue();
        extractor.CanHandle(".PDF").ShouldBeTrue();
        extractor.CanHandle(".png").ShouldBeFalse();
        extractor.CanHandle(".docx").ShouldBeFalse();
        extractor.CanHandle(string.Empty).ShouldBeFalse();

        extractor.Priority.ShouldBeGreaterThan(MarkdownProviderPriorities.Fallback);
    }
}

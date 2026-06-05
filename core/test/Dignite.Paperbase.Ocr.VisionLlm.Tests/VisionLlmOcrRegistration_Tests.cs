using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;
using Dignite.Paperbase.Ocr;

namespace Dignite.Paperbase.Ocr.VisionLlm;

/// <summary>
/// Guards the ABP conventional-registration metadata for the VisionLlm services.
/// <para>
/// The pure-unit provider tests construct <see cref="VisionLlmOcrProvider"/> with <c>new(...)</c> and a
/// mocked <see cref="IPdfRasterizer"/>, so they cannot catch a missing DI registration. ABP exposes an
/// interface <c>IFoo</c> by convention only when the class name ends with <c>Foo</c>:
/// <c>VisionLlmOcrProvider</c> ends with <c>OcrProvider</c> (matches <see cref="IOcrProvider"/>), but
/// <c>PdfToImageRasterizer</c> does NOT end with <c>PdfRasterizer</c>, so <see cref="IPdfRasterizer"/> is
/// only exposed via the explicit <c>[ExposeServices]</c>. These tests assert both interfaces are exposed,
/// failing loudly if the attribute is ever dropped (which would crash resolution at runtime once the
/// VisionLlm module is enabled in a host).
/// </para>
/// </summary>
public class VisionLlmOcrRegistration_Tests
{
    [Fact]
    public void Rasterizer_Exposes_IPdfRasterizer()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PdfToImageRasterizer))
            .ShouldContain(typeof(IPdfRasterizer));
    }

    [Fact]
    public void Provider_Exposes_IOcrProvider()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(VisionLlmOcrProvider))
            .ShouldContain(typeof(IOcrProvider));
    }
}

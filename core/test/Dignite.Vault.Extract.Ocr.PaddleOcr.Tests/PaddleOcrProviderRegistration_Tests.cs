using Dignite.Vault.Extract.Ocr;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Ocr.PaddleOcr;

/// <summary>
/// Guards the ABP conventional-registration metadata for <see cref="PaddleOcrProvider"/>. The behavior
/// tests build it with <c>new(...)</c>, so they cannot catch a dropped marker. The class name ends with
/// <c>OcrProvider</c>, so <see cref="IOcrProvider"/> is exposed by convention; this asserts it stays exposed
/// and transient, otherwise enabling the PaddleOCR module in a host would fail to resolve the provider.
/// </summary>
public class PaddleOcrProviderRegistration_Tests
{
    [Fact]
    public void Provider_Exposes_IOcrProvider()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PaddleOcrProvider))
            .ShouldContain(typeof(IOcrProvider));
    }

    [Fact]
    public void Provider_Is_Conventionally_Registered_Via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(PaddleOcrProvider)).ShouldBeTrue();
    }
}

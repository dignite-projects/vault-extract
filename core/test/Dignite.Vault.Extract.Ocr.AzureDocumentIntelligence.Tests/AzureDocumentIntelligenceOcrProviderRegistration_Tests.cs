using Dignite.Vault.Extract.Ocr;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence;

/// <summary>
/// Guards the ABP conventional-registration metadata for <see cref="AzureDocumentIntelligenceOcrProvider"/>.
/// The class name ends with <c>OcrProvider</c>, so <see cref="IOcrProvider"/> is exposed by convention; this
/// asserts it stays exposed and transient, otherwise enabling the Azure DI module in a host would fail to
/// resolve the provider.
/// </summary>
public class AzureDocumentIntelligenceOcrProviderRegistration_Tests
{
    [Fact]
    public void Provider_Exposes_IOcrProvider()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(AzureDocumentIntelligenceOcrProvider))
            .ShouldContain(typeof(IOcrProvider));
    }

    [Fact]
    public void Provider_Is_Conventionally_Registered_Via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(AzureDocumentIntelligenceOcrProvider)).ShouldBeTrue();
    }
}

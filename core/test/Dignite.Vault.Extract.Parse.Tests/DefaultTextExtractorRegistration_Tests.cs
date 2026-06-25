using Dignite.Vault.Extract.Abstractions.Parse;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Guards the ABP conventional-registration metadata for <see cref="DefaultTextExtractor"/>. The behavior
/// tests construct it with <c>new(...)</c>, so they cannot catch a dropped DI marker. ABP derives the
/// lifetime and the exposed <see cref="ITextExtractor"/> from the markers; losing either would leave the
/// core module unable to resolve <see cref="ITextExtractor"/> at runtime while the behavior tests stayed
/// green.
/// </summary>
public class DefaultTextExtractorRegistration_Tests
{
    [Fact]
    public void Extractor_Exposes_ITextExtractor()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(DefaultTextExtractor))
            .ShouldContain(typeof(ITextExtractor));
    }

    [Fact]
    public void Extractor_Is_Conventionally_Registered_Via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(DefaultTextExtractor)).ShouldBeTrue();
    }
}

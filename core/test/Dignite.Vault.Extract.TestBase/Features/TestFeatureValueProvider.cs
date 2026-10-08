using System.Collections.Concurrent;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Features;

namespace Dignite.Vault.Extract.Features;

/// <summary>
/// An in-memory feature override a test can write to. It spares the test layer the Feature Management module
/// and the database that module needs.
/// <para>
/// Registered <b>last</b> in <c>AbpFeatureOptions.ValueProviders</c>, which gives it the final say
/// (<c>FeatureChecker</c> walks the providers in reverse registration order and takes the first non-null
/// answer). Holding nothing by default it answers null for everything, so every feature resolves to its
/// definition default exactly as it would without this provider.
/// </para>
/// </summary>
public class TestFeatureValueProvider : IFeatureValueProvider, ISingletonDependency
{
    public const string ProviderName = "Test";

    private readonly ConcurrentDictionary<string, string?> _values = new();

    public string Name => ProviderName;

    /// <summary>Overrides one feature. A null value removes the override rather than forcing null.</summary>
    public void Set(string name, string? value)
    {
        if (value == null)
        {
            _values.TryRemove(name, out _);
        }
        else
        {
            _values[name] = value;
        }
    }

    public void Clear()
    {
        _values.Clear();
    }

    public Task<string?> GetOrNullAsync(FeatureDefinition feature)
    {
        return Task.FromResult(_values.TryGetValue(feature.Name, out var value) ? value : null);
    }
}

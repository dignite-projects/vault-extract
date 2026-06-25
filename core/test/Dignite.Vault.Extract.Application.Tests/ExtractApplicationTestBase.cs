using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/* Inherit from this class for your application layer tests.
 */
public abstract class ExtractApplicationTestBase<TStartupModule> : ExtractTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}

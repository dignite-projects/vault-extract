using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/* Inherit from this class for your domain layer tests.
 */
public abstract class ExtractDomainTestBase<TStartupModule> : ExtractTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}

using Volo.Abp.AspNetCore.TestBase;

namespace Dignite.Vault.Extract;

/// <summary>
/// Base class for HttpApi integration tests. Builds a self-contained ASP.NET Core TestServer from
/// <see cref="ExtractHttpApiTestModule"/> and exposes a <c>Client</c>
/// (<see cref="System.Net.Http.HttpClient"/>) bound to it.
/// </summary>
/// <remarks>
/// Uses <see cref="AbpAspNetCoreIntegratedTestBase{TStartupModule}"/>, which bootstraps the test server
/// directly from the startup module. The newer <c>AbpWebApplicationFactoryIntegratedTest</c> is recommended
/// for full host apps but needs a runnable host entry point; the integrated base is the right fit for a
/// module-level controller test.
/// </remarks>
#pragma warning disable CS0618 // Recommended replacement requires a runnable host entry point; see remarks.
public abstract class ExtractHttpApiTestBase : AbpAspNetCoreIntegratedTestBase<ExtractHttpApiTestModule>
#pragma warning restore CS0618
{
}

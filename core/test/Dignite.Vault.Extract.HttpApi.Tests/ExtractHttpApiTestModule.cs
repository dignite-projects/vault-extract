using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc.AntiForgery;
using Volo.Abp.AspNetCore.TestBase;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/// <summary>
/// Startup module for the HttpApi integration tests: layers the REST egress
/// (<see cref="ExtractHttpApiModule"/>) on top of the reused EF Core test module (in-memory SQLite +
/// Application + Domain + always-allow authorization + data seeding) and wires the minimal ASP.NET Core MVC
/// pipeline so the controllers are routable through a real <c>HttpClient</c>.
/// </summary>
[DependsOn(
    typeof(AbpAspNetCoreTestBaseModule),
    typeof(ExtractHttpApiModule),
    typeof(ExtractEntityFrameworkCoreTestModule)
)]
public class ExtractHttpApiTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The tests post JSON without an antiforgery token; disable auto-validation for the test host so
        // unsafe-method REST calls are not rejected before reaching the controller.
        Configure<AbpAntiForgeryOptions>(options => options.AutoValidate = false);
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();

        app.UseRouting();
        app.UseAuthorization();
        app.UseConfiguredEndpoints();
    }
}

using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(ExtractAbstractionsModule),
    typeof(ExtractDomainModule),
    typeof(ExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule),
    typeof(AbpMapperlyModule)
    )]
public class ExtractApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddMapperlyObjectMapper<ExtractApplicationModule>();

        var configuration = context.Services.GetConfiguration();
        Configure<ExtractBehaviorOptions>(configuration.GetSection("Vault:ExtractBehavior"));
    }
}

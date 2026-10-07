using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractAbstractionsModule),
    typeof(VaultExtractDomainModule),
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule),
    typeof(AbpNotificationsModule),   // operator notifications for the uploader (#680)
    typeof(AbpMapperlyModule)
    )]
public class VaultExtractApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddMapperlyObjectMapper<VaultExtractApplicationModule>();

        var configuration = context.Services.GetConfiguration();
        Configure<VaultExtractBehaviorOptions>(configuration.GetSection("Vault:ExtractBehavior"));
    }
}

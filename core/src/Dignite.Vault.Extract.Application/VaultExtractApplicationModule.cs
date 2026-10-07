using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Notifications;
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

        // Default routing for the uploader notifications (#680): live push. A module owns the default for its own
        // notifications and the host, configured last, overrides it (later writes win). It has to be a rule rather than
        // left to the host: a process running the notification framework stateless (no inbox store) fails at startup
        // for any defined notification that resolves to no channel, which would break every host that embeds
        // Application without configuring routing. A host that does not run the SignalR notifier only gets a startup
        // warning naming the channel.
        Configure<NotificationRoutingOptions>(options =>
            options.ForNotifications(
                VaultExtractNotificationNames.All,
                VaultExtractNotificationConsts.SignalRChannelName));
    }
}

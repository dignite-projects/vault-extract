using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Client;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(ExtractApplicationContractsModule),
    typeof(AbpHttpClientModule))]
public class ExtractHttpApiClientModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpClientProxies(
            typeof(ExtractApplicationContractsModule).Assembly,
            ExtractRemoteServiceConsts.RemoteServiceName
        );

        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<ExtractHttpApiClientModule>();
        });

    }
}

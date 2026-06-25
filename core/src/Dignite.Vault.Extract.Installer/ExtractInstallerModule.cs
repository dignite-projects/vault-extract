using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(AbpVirtualFileSystemModule)
    )]
public class ExtractInstallerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<ExtractInstallerModule>();
        });
    }
}

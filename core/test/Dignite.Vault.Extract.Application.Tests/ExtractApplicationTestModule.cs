using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(ExtractApplicationModule),
    typeof(ExtractDomainTestModule)
    )]
public class ExtractApplicationTestModule : AbpModule
{
}

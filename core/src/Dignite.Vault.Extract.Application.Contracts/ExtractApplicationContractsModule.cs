using Volo.Abp.Application;
using Volo.Abp.Modularity;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(ExtractDomainSharedModule),
    typeof(AbpDddApplicationContractsModule),
    typeof(AbpAuthorizationModule)
    )]
public class ExtractApplicationContractsModule : AbpModule
{

}

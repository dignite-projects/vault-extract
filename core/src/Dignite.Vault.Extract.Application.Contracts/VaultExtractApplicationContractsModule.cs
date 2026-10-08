using Volo.Abp.Application;
using Volo.Abp.Features;
using Volo.Abp.Modularity;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractDomainSharedModule),
    typeof(AbpDddApplicationContractsModule),
    typeof(AbpAuthorizationModule),
    // The package was already referenced; the module is what registers the [RequiresFeature] interceptor on
    // VaultExtractAppService, so depend on it explicitly rather than relying on a transitive module.
    typeof(AbpFeaturesModule)
    )]
public class VaultExtractApplicationContractsModule : AbpModule
{

}

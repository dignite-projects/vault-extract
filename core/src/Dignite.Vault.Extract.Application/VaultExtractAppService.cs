using Dignite.Vault.Extract.Features;
using Dignite.Vault.Extract.Localization;
using Volo.Abp.Application.Services;
using Volo.Abp.Features;

namespace Dignite.Vault.Extract;

// On the base class so that every application service - and the REST controllers and MCP tools, which only call
// them through their interfaces - is gated at once. VaultExtractAppServiceFeature_Tests fails a service that does
// not derive from it. Background jobs and event handlers never go through an application service and must not
// be given this attribute; see VaultExtractFeatures.Enable for why.
[RequiresFeature(VaultExtractFeatures.Enable)]
public abstract class VaultExtractAppService : ApplicationService
{
    protected VaultExtractAppService()
    {
        LocalizationResource = typeof(VaultExtractResource);
        ObjectMapperContext = typeof(VaultExtractApplicationModule);
    }
}

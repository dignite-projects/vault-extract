using Dignite.Vault.Extract.Localization;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract;

public abstract class ExtractAppService : ApplicationService
{
    protected ExtractAppService()
    {
        LocalizationResource = typeof(ExtractResource);
        ObjectMapperContext = typeof(ExtractApplicationModule);
    }
}

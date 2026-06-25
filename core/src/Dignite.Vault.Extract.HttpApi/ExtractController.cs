using Dignite.Vault.Extract.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace Dignite.Vault.Extract;

public abstract class ExtractController : AbpControllerBase
{
    protected ExtractController()
    {
        LocalizationResource = typeof(ExtractResource);
    }
}

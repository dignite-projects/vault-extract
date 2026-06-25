using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Ocr.VisionLlm;

[DependsOn(typeof(ExtractOcrModule))]
public class ExtractVisionLlmOcrModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<VisionLlmOcrOptions>(
            configuration.GetSection("VisionLlmOcr"));
    }
}

using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Paperbase.Ocr.VisionLlm;

[DependsOn(typeof(PaperbaseOcrModule))]
public class PaperbaseVisionLlmOcrModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<VisionLlmOcrOptions>(
            configuration.GetSection("VisionLlmOcr"));
    }
}

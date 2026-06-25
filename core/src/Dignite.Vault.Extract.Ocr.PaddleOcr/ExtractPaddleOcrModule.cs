using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Ocr.PaddleOcr;

[DependsOn(typeof(ExtractOcrModule))]
public class ExtractPaddleOcrModule : AbpModule
{
    internal const string HttpClientName = "PaddleOcr";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<PaddleOcrOptions>(
            configuration.GetSection("PaddleOcr"));

        // Uses a named HttpClient, with timeout read from PaddleOcrOptions.TimeoutSeconds.
        // PP-StructureV3 may take far longer than the default 100s on multi-page image PDFs on CPU, so
        // this raises the limit.
        context.Services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<PaddleOcrOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });
    }
}

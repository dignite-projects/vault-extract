using ElBruno.MarkItDotNet;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;

[DependsOn(typeof(ExtractParseModule))]
public class ExtractParseElBrunoMarkItDownModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Registers ElBruno.MarkItDotNet's internal ConverterRegistry, MarkdownService, and 12 built-in converters.
        context.Services.AddMarkItDotNet();
    }
}

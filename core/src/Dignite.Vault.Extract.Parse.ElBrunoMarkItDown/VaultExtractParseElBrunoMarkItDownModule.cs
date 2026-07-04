using ElBruno.MarkItDotNet;
using ElBruno.MarkItDotNet.Excel;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;

[DependsOn(typeof(VaultExtractParseModule))]
public class VaultExtractParseElBrunoMarkItDownModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Registers ElBruno.MarkItDotNet's internal ConverterRegistry, MarkdownService, and built-in converters.
        context.Services.AddMarkItDotNet();
        // XLSX is distributed as a satellite plugin rather than a core converter. Register it in the same module
        // so every host that enables the ElBruno catch-all gets the upload contract's spreadsheet capability.
        context.Services.AddMarkItDotNetExcel();
    }
}

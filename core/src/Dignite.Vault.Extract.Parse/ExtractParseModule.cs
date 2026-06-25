using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.Ocr;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.Parse;

[DependsOn(typeof(ExtractAbstractionsModule), typeof(ExtractOcrModule))]
public class ExtractParseModule : AbpModule
{
}

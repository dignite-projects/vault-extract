using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.FlexFields.Localization;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract.FlexFields;

/// <summary>
/// Vault Extract's own extensions to the FlexFields kernel (#558): field types the kernel does not
/// ship, and the vocabulary that describes them.
/// <para>
/// Field types register themselves - <c>FieldTypeBase</c> is an <c>ITransientDependency</c> - so this
/// module declares no services of its own beyond its localization resource.
/// </para>
/// </summary>
[DependsOn(
    typeof(AbpFlexFieldsAbstractionsModule),
    // The CKEditor bolt-on field type, which carries what v2 called LongText. Registered here so the
    // whole set of field types a Vault Extract Field may name resolves from one module.
    typeof(Dignite.Abp.FlexFields.CKEditor.AbpFlexFieldsCKEditorModule)
    )]
public class VaultExtractFlexFieldsModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<VaultExtractFlexFieldsModule>();
        });

        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<VaultExtractFlexFieldsResource>("en")
                .AddVirtualJson("/Localization/FlexFields");
        });
    }
}

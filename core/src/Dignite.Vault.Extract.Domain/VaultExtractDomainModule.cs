using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.FlexFields;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(AbpDddDomainModule),
    typeof(AbpBlobStoringModule),
    typeof(VaultExtractAbstractionsModule),
    typeof(VaultExtractDomainSharedModule),
    // Field architecture v3 (#558): IFlexField / IFlexFieldProvider and the provider-neutral
    // FlexFieldValidator + FlexFieldValueMigrator open generics the kernel registers here.
    typeof(AbpFlexFieldsDomainModule),
    // Vault Extract's own field types (Tags). Depended on from Domain rather than left to the host,
    // because a Field row can name Tags and every consumer of the kernel's IFieldTypeResolver has to be
    // able to resolve it - a field type missing from the container is a runtime failure, not a
    // configuration choice.
    typeof(VaultExtractFlexFieldsModule)
)]
public class VaultExtractDomainModule : AbpModule
{
}

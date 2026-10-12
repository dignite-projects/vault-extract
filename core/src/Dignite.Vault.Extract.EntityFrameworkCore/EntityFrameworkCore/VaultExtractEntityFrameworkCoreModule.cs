using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

[DependsOn(
    typeof(VaultExtractDomainModule),
    typeof(AbpEntityFrameworkCoreModule),
    // Field architecture v3 (#558): the relational index/query base classes DocumentFlexFieldIndexManager
    // and DocumentFlexFieldQueryExecutor derive from.
    typeof(Abp.FlexFields.EntityFrameworkCore.AbpFlexFieldsEntityFrameworkCoreModule)
)]
public class VaultExtractEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<VaultExtractDbContext>(options =>
        {
            options.AddDefaultRepositories();

            options.AddRepository<Document, EfCoreDocumentRepository>();
            options.AddRepository<DocumentType, EfCoreDocumentTypeRepository>();
            options.AddRepository<Field, EfCoreFieldRepository>();
            options.AddRepository<Cabinet, EfCoreCabinetRepository>();
            // #216: PipelineRun was promoted to an independent aggregate root.
            options.AddRepository<DocumentPipelineRun, EfCoreDocumentPipelineRunRepository>();
        });
    }
}

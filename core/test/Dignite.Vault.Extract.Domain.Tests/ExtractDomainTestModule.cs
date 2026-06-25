using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(ExtractDomainModule),
    typeof(ExtractTestBaseModule)
)]
public class ExtractDomainTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Manager depends on IDocumentPipelineRunRepository (#216). Use the closure-state fake shared by
        // Domain.Tests so QueueAsync / DeriveLifecycle DB-query paths can run completely in memory.
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

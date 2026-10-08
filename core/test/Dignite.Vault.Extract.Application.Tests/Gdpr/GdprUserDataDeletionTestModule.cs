using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Gdpr;

/// <summary>
/// The erasure handler with its two seams replaced: the document store (this stack has no persistence; what the
/// repository really does is asserted against SQLite in <c>EfCoreDocumentRepositoryAnonymizeUploader_Tests</c>) and
/// the tenant store (the handler's question to it is the thing under test, not how tenants are persisted).
/// </summary>
[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class GdprUserDataDeletionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<ITenantStore>());
    }
}

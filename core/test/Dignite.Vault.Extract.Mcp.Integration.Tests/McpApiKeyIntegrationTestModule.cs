using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.BlobStoring;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.Testing;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Integration-test host for the MCP API-key channel (#432). Unlike the shared <c>VaultExtractTestBase</c>, this
/// module <b>does not</b> call <c>AddAlwaysAllowAuthorization</c>: it boots the real Application + EF stack plus
/// real ABP PermissionManagement (Domain + EF), so the permission checker resolves grants from the store by the
/// principal's user id — exactly the seam the API-key channel depends on and the #430 TestServer tests (with a
/// stub scheme) could not cover. Two DbContexts (Extract + PermissionManagement) share one in-memory SQLite
/// connection so a granted service-account user id and the searched documents live in the same database.
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(VaultExtractApplicationModule),
    typeof(VaultExtractEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqliteModule),
    typeof(AbpPermissionManagementDomainModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule)
)]
public class McpApiKeyIntegrationTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpSqliteOptions>(x => x.BusyTimeout = null);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep permissions purely code-based (static providers): no DB-persisted permission definitions and no
        // dynamic store, so the test needs no definition seeding / distributed lock — only grant rows matter.
        Configure<PermissionManagementOptions>(options =>
        {
            options.SaveStaticPermissionsToDatabase = false;
            options.IsDynamicPermissionStoreEnabled = false;
        });

        // DocumentAppService depends on the document BLOB container; this test never touches blob storage
        // (it only searches), so substitute it rather than wiring a real provider — same pattern as the
        // Application/EF tests. The real repositories + real DB stay in place for the search path.
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());

        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

        var sqliteConnection = CreateDatabaseAndGetConnection();

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                configurationContext.UseSqlite(sqliteConnection);
            });
        });
    }

    private static SqliteConnection CreateDatabaseAndGetConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // Create both schemas on the SAME open in-memory connection: Extract tables (documents / types) and the
        // ABP permission-grant tables. The connection stays open for the app lifetime so the schema persists.
        new VaultExtractDbContext(
            new DbContextOptionsBuilder<VaultExtractDbContext>().UseSqlite(connection).Options
        ).GetService<IRelationalDatabaseCreator>().CreateTables();

        new PermissionManagementDbContext(
            new DbContextOptionsBuilder<PermissionManagementDbContext>().UseSqlite(connection).Options
        ).GetService<IRelationalDatabaseCreator>().CreateTables();

        return connection;
    }
}

using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

[DependsOn(
    typeof(VaultExtractApplicationTestModule),
    typeof(VaultExtractEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqliteModule)
)]
public class VaultExtractEntityFrameworkCoreTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpSqliteOptions>(x => x.BusyTimeout = null);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

        // VaultExtractApplicationTestModule stands in for the FlexFields services that need real
        // persistence, because that stack has none. This one does, so the migrator stand-in has to go.
        // Unlike the index manager and query executor — implemented in VaultExtractEntityFrameworkCoreModule,
        // which configures after the Application test module and therefore wins — the migrator is the
        // kernel's open-generic FlexFieldValueMigrator<T>, registered by AbpFlexFieldsDomainModule long before
        // the substitute. Leave it and the substitute wins here: a field rename becomes a silent no-op, the
        // bag keeps the old key, and every value under it turns unreachable.
        context.Services.RemoveAll<IFlexFieldValueMigrator<Document>>();

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

        new VaultExtractDbContext(
            new DbContextOptionsBuilder<VaultExtractDbContext>().UseSqlite(connection).Options
        ).GetService<IRelationalDatabaseCreator>().CreateTables();

        return connection;
    }
}

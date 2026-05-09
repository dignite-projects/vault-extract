using System;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.EntityFrameworkCore;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Chat;

[DependsOn(
    typeof(PaperbaseApplicationTestModule),
    typeof(PaperbaseEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqliteModule)
)]
public class DocumentChatAppServiceTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpSqliteOptions>(x => x.BusyTimeout = null);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

        var sqliteConnection = CreateDatabaseAndGetConnection();
        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                configurationContext.UseSqlite(sqliteConnection);
            });
        });

        // Substituted external dependencies — substitute IDocumentRepository so
        // CreateConversationAsync can validate "document exists" without seeding.
        // IChatClient / IDocumentKnowledgeIndex / IEmbeddingGenerator stay substituted
        // so CI never reaches a real LLM or vector store.
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentKnowledgeIndex>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        // Summarizer client for ChatCompactionStrategyFactory. ChatCompactionOptions
        // defaults to disabled, so no test reaches the summarizer — registration just
        // satisfies [FromKeyedServices] DI resolution at AppService activation.
        context.Services.AddKeyedSingleton(
            PaperbaseAIConsts.SummarizerChatClientKey,
            Substitute.For<IChatClient>());
    }

    private static SqliteConnection CreateDatabaseAndGetConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        new PaperbaseDbContext(
            new DbContextOptionsBuilder<PaperbaseDbContext>().UseSqlite(connection).Options
        ).GetService<IRelationalDatabaseCreator>().CreateTables();

        return connection;
    }
}

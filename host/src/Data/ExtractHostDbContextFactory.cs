using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dignite.Vault.Extract.Host.Data;

public class ExtractHostDbContextFactory : IDesignTimeDbContextFactory<ExtractHostDbContext>
{
    public ExtractHostDbContext CreateDbContext(string[] args)
    {
        ExtractHostGlobalFeatureConfigurator.Configure();
        ExtractHostModuleExtensionConfigurator.Configure();

        ExtractHostEfCoreEntityExtensionMappings.Configure();
        var configuration = BuildConfiguration();

        var builder = new DbContextOptionsBuilder<ExtractHostDbContext>()
            .UseSqlServer(configuration.GetConnectionString("Default"));

        return new ExtractHostDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();

        return builder.Build();
    }
}

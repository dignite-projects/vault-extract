using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dignite.Vault.Extract.Host.Data;

public class VaultExtractHostDbContextFactory : IDesignTimeDbContextFactory<VaultExtractHostDbContext>
{
    public VaultExtractHostDbContext CreateDbContext(string[] args)
    {
        VaultExtractHostGlobalFeatureConfigurator.Configure();
        VaultExtractHostModuleExtensionConfigurator.Configure();

        VaultExtractHostEfCoreEntityExtensionMappings.Configure();
        var configuration = BuildConfiguration();

        var builder = new DbContextOptionsBuilder<VaultExtractHostDbContext>()
            .UseSqlServer(configuration.GetConnectionString("Default"));

        return new VaultExtractHostDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.secrets.json", optional: true)
            .AddEnvironmentVariables();

        return builder.Build();
    }
}

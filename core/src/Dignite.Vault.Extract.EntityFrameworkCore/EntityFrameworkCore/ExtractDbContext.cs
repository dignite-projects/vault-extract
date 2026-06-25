using Dignite.Vault.Extract.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

[ConnectionStringName(ExtractDbProperties.ConnectionStringName)]
public class ExtractDbContext : AbpDbContext<ExtractDbContext>, IExtractDbContext
{
    public DbSet<Document> Documents { get; set; }
    public DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; set; }
    public DbSet<DocumentSegment> DocumentSegments { get; set; }
    public DbSet<DocumentType> DocumentTypes { get; set; }
    public DbSet<FieldDefinition> FieldDefinitions { get; set; }
    public DbSet<ExportTemplate> ExportTemplates { get; set; }
    public DbSet<Cabinet> Cabinets { get; set; }

    public ExtractDbContext(DbContextOptions<ExtractDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureExtract();
    }
}

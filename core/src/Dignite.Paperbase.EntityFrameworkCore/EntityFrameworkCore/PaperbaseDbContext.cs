using Dignite.Paperbase.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.EntityFrameworkCore;

[ConnectionStringName(PaperbaseDbProperties.ConnectionStringName)]
public class PaperbaseDbContext : AbpDbContext<PaperbaseDbContext>, IPaperbaseDbContext
{
    public DbSet<Document> Documents { get; set; }
    public DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; set; }
    public DbSet<DocumentType> DocumentTypes { get; set; }
    public DbSet<FieldDefinition> FieldDefinitions { get; set; }
    public DbSet<Cabinet> Cabinets { get; set; }

    public PaperbaseDbContext(DbContextOptions<PaperbaseDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigurePaperbase();
    }
}

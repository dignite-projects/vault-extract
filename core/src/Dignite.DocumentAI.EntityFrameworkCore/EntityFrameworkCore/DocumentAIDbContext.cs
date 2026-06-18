using Dignite.DocumentAI.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.DocumentAI.EntityFrameworkCore;

[ConnectionStringName(DocumentAIDbProperties.ConnectionStringName)]
public class DocumentAIDbContext : AbpDbContext<DocumentAIDbContext>, IDocumentAIDbContext
{
    public DbSet<Document> Documents { get; set; }
    public DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; set; }
    public DbSet<DocumentSegment> DocumentSegments { get; set; }
    public DbSet<DocumentType> DocumentTypes { get; set; }
    public DbSet<FieldDefinition> FieldDefinitions { get; set; }
    public DbSet<ExportTemplate> ExportTemplates { get; set; }
    public DbSet<Cabinet> Cabinets { get; set; }

    public DocumentAIDbContext(DbContextOptions<DocumentAIDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureDocumentAI();
    }
}

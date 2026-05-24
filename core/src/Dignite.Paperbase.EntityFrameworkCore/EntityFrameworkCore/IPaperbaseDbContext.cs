using Dignite.Paperbase.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.EntityFrameworkCore;

[ConnectionStringName(PaperbaseDbProperties.ConnectionStringName)]
public interface IPaperbaseDbContext : IEfCoreDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; }
    DbSet<DocumentType> DocumentTypes { get; }
    DbSet<FieldDefinition> FieldDefinitions { get; }
    DbSet<Cabinet> Cabinets { get; }
}

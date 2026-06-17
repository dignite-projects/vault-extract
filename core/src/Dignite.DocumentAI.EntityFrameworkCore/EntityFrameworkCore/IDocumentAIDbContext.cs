using Dignite.DocumentAI.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.DocumentAI.EntityFrameworkCore;

[ConnectionStringName(DocumentAIDbProperties.ConnectionStringName)]
public interface IDocumentAIDbContext : IEfCoreDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; }
    DbSet<DocumentFigure> DocumentFigures { get; }
    DbSet<DocumentType> DocumentTypes { get; }
    DbSet<FieldDefinition> FieldDefinitions { get; }
    DbSet<ExportTemplate> ExportTemplates { get; }
    DbSet<Cabinet> Cabinets { get; }
}

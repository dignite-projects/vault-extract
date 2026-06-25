using System;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents.Exports;

public interface IExportTemplateRepository : IRepository<ExportTemplate, Guid>
{
    /// <summary>Finds a template by current layer and Name, used for duplicate checks on creation.</summary>
    Task<ExportTemplate?> FindByNameAsync(string name, CancellationToken cancellationToken = default);
}

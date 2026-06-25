using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents.Exports;

public class EfCoreExportTemplateRepository
    : EfCoreRepository<ExtractDbContext, ExportTemplate, Guid>, IExportTemplateRepository
{
    public EfCoreExportTemplateRepository(IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<ExportTemplate?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            t => t.Name == name,
            GetCancellationToken(cancellationToken));
    }
}

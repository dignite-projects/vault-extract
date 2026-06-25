using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public class EfCoreDocumentTypeRepository
    : EfCoreRepository<ExtractDbContext, DocumentType, Guid>, IDocumentTypeRepository
{
    public EfCoreDocumentTypeRepository(IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<DocumentType?> FindByTypeCodeAsync(
        string typeCode,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            t => t.TypeCode == typeCode,
            GetCancellationToken(cancellationToken));
    }
}

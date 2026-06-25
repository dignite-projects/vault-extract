using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents.Fields;

public class EfCoreFieldDefinitionRepository
    : EfCoreRepository<ExtractDbContext, FieldDefinition, Guid>, IFieldDefinitionRepository
{
    public EfCoreFieldDefinitionRepository(IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<FieldDefinition>> GetListAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(f => f.DocumentTypeId == documentTypeId)
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<FieldDefinition?> FindByNameAsync(
        Guid documentTypeId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            f => f.DocumentTypeId == documentTypeId && f.Name == name,
            GetCancellationToken(cancellationToken));
    }
}

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
    : EfCoreRepository<VaultExtractDbContext, FieldDefinition, Guid>, IFieldDefinitionRepository
{
    public EfCoreFieldDefinitionRepository(IDbContextProvider<VaultExtractDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<FieldDefinition>> GetListAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(f => f.DocumentTypeId == documentTypeId)
            // #499: ThenBy(Name) makes this a total order. DisplayOrder defaults to 0, so ties are ordinary
            // (a pack import, an admin who never set an order) and were previously broken by whatever the DB
            // returned. Since the export now derives its columns from this very sequence, an unstable tail
            // would mean the same data exports in a different column order on different days — and would
            // disagree with the operator list, which renders from the same call. Name is unique per
            // (TenantId, DocumentTypeId), so the order is total.
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Name)
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents;

public class EfCoreCabinetRepository
    : EfCoreRepository<PaperbaseDbContext, Cabinet, Guid>, ICabinetRepository
{
    public EfCoreCabinetRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<Cabinet>> GetListAsync(CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .OrderBy(c => c.DisplayName)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<Cabinet?> FindByDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            c => c.DisplayName == displayName,
            GetCancellationToken(cancellationToken));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents.Cabinets;

public class EfCoreCabinetRepository
    : EfCoreRepository<ExtractDbContext, Cabinet, Guid>, ICabinetRepository
{
    public EfCoreCabinetRepository(IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<Cabinet?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            c => c.Name == name,
            GetCancellationToken(cancellationToken));
    }
}

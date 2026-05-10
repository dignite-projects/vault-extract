using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts.Contracts;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Contracts.EntityFrameworkCore;

public class EfCoreContractRepository :
    EfCoreRepository<IContractsDbContext, Contract, Guid>,
    IContractRepository
{
    public EfCoreContractRepository(IDbContextProvider<IContractsDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<Contract?> FindByDocumentIdAsync(Guid documentId)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(x => x.DocumentId == documentId);
    }

    public virtual async Task<List<Contract>> FindByContractNumberAsync(
        string contractNumber,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(x => x.ContractNumber == contractNumber)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<Contract>> GetListByPartyNameAsync(
        string partyName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(x =>
                x.PartyAName == partyName
                || x.PartyBName == partyName
                || x.CounterpartyName == partyName)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }
}

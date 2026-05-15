using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Contracts.EntityFrameworkCore;

public class EfCoreContractRepository :
    EfCoreRepository<IPaperbaseContractsDbContext, Contract, Guid>,
    IContractRepository
{
    public EfCoreContractRepository(IDbContextProvider<IPaperbaseContractsDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<Contract?> FindByDocumentIdAsync(Guid documentId)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(x => x.DocumentId == documentId);
    }

    /// <summary>
    /// 硬伤一 fix: lookup goes through <see cref="Contract.NormalizedContractNumber"/> so
    /// L2 RelationDiscovery's normalized identifier value ("HT2024001") matches the stored
    /// canonical form, regardless of casing / separator / width variations in the original
    /// document. The repository accepts an ALREADY-normalized argument — callers (L2 service,
    /// ContractIdentifierProvider) are responsible for invoking
    /// <see cref="Dignite.Paperbase.Documents.DocumentIdentifierNormalization.Normalize"/>
    /// before calling. Documented at the IContractRepository interface.
    /// </summary>
    public virtual async Task<List<Contract>> FindByContractNumberAsync(
        string contractNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contractNumber))
        {
            return new List<Contract>();
        }

        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(x => x.NormalizedContractNumber == contractNumber)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }
}

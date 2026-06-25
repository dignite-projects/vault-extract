using System;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents.Cabinets;

public interface ICabinetRepository : IRepository<Cabinet, Guid>
{
    /// <summary>
    /// Finds a cabinet by exact name in the current layer, used for CRUD duplicate checks. Only active
    /// cabinets are queried: Cabinet has no recycle bin, and soft-deleted cabinet names may be reused by
    /// new cabinets because the unique index is filtered with <c>IsDeleted = 0</c>.
    /// </summary>
    Task<Cabinet?> FindByNameAsync(string name, CancellationToken cancellationToken = default);
}

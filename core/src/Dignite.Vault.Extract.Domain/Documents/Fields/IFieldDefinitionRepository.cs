using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents.Fields;

public interface IFieldDefinitionRepository : IRepository<FieldDefinition, Guid>
{
    /// <summary>
    /// Queries field definitions under a document type in the current ambient tenant layer, matched by
    /// the internal association <paramref name="documentTypeId"/> (#207) and ordered by
    /// <c>DisplayOrder</c>. Field extraction, management, and MCP read paths share this query.
    /// <para>
    /// Isolation is enforced by the ambient <c>IMultiTenant</c> filter, without cross-layer reads.
    /// Background and event paths, such as field extraction, must call
    /// <c>ICurrentTenant.Change(targetTenantId)</c> before invoking this so the ambient layer matches
    /// <c>Document.TenantId</c>.
    /// </para>
    /// </summary>
    Task<List<FieldDefinition>> GetListAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition?> FindByNameAsync(
        Guid documentTypeId,
        string name,
        CancellationToken cancellationToken = default);
}

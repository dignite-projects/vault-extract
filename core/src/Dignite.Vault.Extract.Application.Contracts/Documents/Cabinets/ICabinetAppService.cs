using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Cabinet management (#194). Two independent single layers, following
/// <see cref="IDocumentTypeAppService"/>:
/// <para>
/// <see cref="GetListAsync"/> returns all cabinets in the current layer: host admins see host
/// cabinets, tenant admins see their own tenant cabinets, with no cross-layer union. Create / Update /
/// Delete affect only the current layer (TenantId == CurrentTenant.Id).
/// </para>
/// <para>
/// Difference from <see cref="IDocumentTypeAppService"/>: there is <b>no recycle bin</b> because a
/// mistakenly deleted cabinet can simply be recreated and has no cascading field definitions.
/// <b>Deletion is not blocked by InUse</b> because cabinets are orthogonal to pipelines, but deleting a
/// cabinet <b>atomically clears</b> CabinetId on all documents in it so they fall back to
/// "uncategorized", leaving no dangling references and not affecting classification / extraction.
/// </para>
/// </summary>
public interface ICabinetAppService : IApplicationService
{
    Task<List<CabinetDto>> GetListAsync();

    Task<CabinetDto> CreateAsync(CreateCabinetDto input);

    Task<CabinetDto> UpdateAsync(Guid id, UpdateCabinetDto input);

    Task DeleteAsync(Guid id);
}

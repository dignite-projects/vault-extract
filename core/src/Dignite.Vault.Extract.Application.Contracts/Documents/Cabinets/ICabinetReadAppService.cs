using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Non-HTTP cabinet read contract for LLM-facing outbound adapters. Reads are available to callers
/// with either Documents.Default or Cabinets.Default, remain tenant/layer isolated, and are bounded
/// at the database query.
/// </summary>
[RemoteService(false)]
public interface ICabinetReadAppService : IApplicationService
{
    Task<CabinetDto> GetAsync(Guid id);

    Task<PagedResultDto<CabinetDto>> GetListAsync();
}

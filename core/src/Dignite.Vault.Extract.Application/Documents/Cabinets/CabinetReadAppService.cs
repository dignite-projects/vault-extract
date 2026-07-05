using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Bounded cabinet read use case for MCP and other LLM-facing outbound adapters. It is deliberately
/// separate from cabinet management so it can accept Documents.Default without widening CRUD access.
/// </summary>
[RemoteService(false)]
public class CabinetReadAppService : VaultExtractAppService, ICabinetReadAppService
{
    private readonly ICabinetRepository _repository;

    public CabinetReadAppService(ICabinetRepository repository)
    {
        _repository = repository;
    }

    public virtual async Task<CabinetDto> GetAsync(Guid id)
    {
        await CheckReadPolicyAsync();

        // GetAsync stays under the ambient IMultiTenant filter, so a cross-layer id is indistinguishable
        // from a nonexistent id and no full-list materialization is needed for one resource read.
        var cabinet = await _repository.GetAsync(id);
        return ObjectMapper.Map<Cabinet, CabinetDto>(cabinet);
    }

    public virtual async Task<PagedResultDto<CabinetDto>> GetListAsync()
    {
        await CheckReadPolicyAsync();

        var query = await _repository.GetQueryableAsync();
        var totalCount = await AsyncExecuter.CountAsync(query);
        var cabinets = await AsyncExecuter.ToListAsync(
            query
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Take(CabinetReadConsts.MaxResultCount));

        return new PagedResultDto<CabinetDto>(
            totalCount,
            ObjectMapper.Map<List<Cabinet>, List<CabinetDto>>(cabinets));
    }

    private async Task CheckReadPolicyAsync()
    {
        // Programmatic OR assertion is mandatory for MCP/reflection dispatch. Cabinet discovery is
        // document-read metadata, while cabinet administrators retain access through Cabinets.Default.
        if (!await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default) &&
            !await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.Cabinets.Default))
        {
            throw new AbpAuthorizationException();
        }
    }
}

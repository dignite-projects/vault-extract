using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Cabinets;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.Cabinets;

[Area("vault-extract")]
[Route("api/vault-extract/cabinets")]
public class CabinetController : ExtractController, ICabinetAppService
{
    private readonly ICabinetAppService _cabinetAppService;

    public CabinetController(ICabinetAppService cabinetAppService)
    {
        _cabinetAppService = cabinetAppService;
    }

    [HttpGet]
    public virtual Task<List<CabinetDto>> GetListAsync()
    {
        return _cabinetAppService.GetListAsync();
    }

    [HttpPost]
    public virtual Task<CabinetDto> CreateAsync([FromBody] CreateCabinetDto input)
    {
        return _cabinetAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    public virtual Task<CabinetDto> UpdateAsync(Guid id, [FromBody] UpdateCabinetDto input)
    {
        return _cabinetAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _cabinetAppService.DeleteAsync(id);
    }
}

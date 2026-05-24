using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Paperbase.HttpApi.Documents;

[Area("paperbase")]
[Route("api/paperbase/cabinets")]
public class CabinetController : PaperbaseController, ICabinetAppService
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

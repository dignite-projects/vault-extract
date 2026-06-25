using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.Fields;

[Area("extract")]
[Route("api/extract/field-definitions")]
public class FieldDefinitionController : ExtractController, IFieldDefinitionAppService
{
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;

    public FieldDefinitionController(IFieldDefinitionAppService fieldDefinitionAppService)
    {
        _fieldDefinitionAppService = fieldDefinitionAppService;
    }

    [HttpGet]
    public virtual Task<List<FieldDefinitionDto>> GetListAsync([FromQuery] GetFieldDefinitionListInput input)
    {
        return _fieldDefinitionAppService.GetListAsync(input);
    }

    [HttpPost]
    public virtual Task<FieldDefinitionDto> CreateAsync([FromBody] CreateFieldDefinitionDto input)
    {
        return _fieldDefinitionAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    public virtual Task<FieldDefinitionDto> UpdateAsync(Guid id, [FromBody] UpdateFieldDefinitionDto input)
    {
        return _fieldDefinitionAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _fieldDefinitionAppService.DeleteAsync(id);
    }

    [HttpPost("{id}/restore")]
    public virtual Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        return _fieldDefinitionAppService.RestoreAsync(id);
    }
}

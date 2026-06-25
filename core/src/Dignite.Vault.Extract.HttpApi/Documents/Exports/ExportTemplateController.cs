using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Exports;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.HttpApi.Documents.Exports;

[Area("vault-extract")]
[Route("api/vault-extract/export-templates")]
public class ExportTemplateController : ExtractController, IExportTemplateAppService
{
    private readonly IExportTemplateAppService _exportTemplateAppService;

    public ExportTemplateController(IExportTemplateAppService exportTemplateAppService)
    {
        _exportTemplateAppService = exportTemplateAppService;
    }

    [HttpGet("{id}")]
    public virtual Task<ExportTemplateDto> GetAsync(Guid id)
    {
        return _exportTemplateAppService.GetAsync(id);
    }

    [HttpGet]
    public virtual Task<List<ExportTemplateDto>> GetListAsync()
    {
        return _exportTemplateAppService.GetListAsync();
    }

    [HttpPost]
    public virtual Task<ExportTemplateDto> CreateAsync([FromBody] CreateExportTemplateDto input)
    {
        return _exportTemplateAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    public virtual Task<ExportTemplateDto> UpdateAsync(Guid id, [FromBody] UpdateExportTemplateDto input)
    {
        return _exportTemplateAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _exportTemplateAppService.DeleteAsync(id);
    }

    [HttpPost("export")]
    public virtual Task<IRemoteStreamContent> ExportAsync([FromBody] ExportDocumentsInput input)
    {
        return _exportTemplateAppService.ExportAsync(input);
    }
}

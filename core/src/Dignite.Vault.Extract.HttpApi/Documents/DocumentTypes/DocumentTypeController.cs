using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.DocumentTypes;

[Area("extract")]
[Route("api/extract/document-types")]
public class DocumentTypeController : ExtractController, IDocumentTypeAppService
{
    private readonly IDocumentTypeAppService _documentTypeAppService;

    public DocumentTypeController(IDocumentTypeAppService documentTypeAppService)
    {
        _documentTypeAppService = documentTypeAppService;
    }

    [HttpGet]
    public virtual Task<List<DocumentTypeDto>> GetVisibleAsync()
    {
        return _documentTypeAppService.GetVisibleAsync();
    }

    [HttpGet("deleted")]
    public virtual Task<List<DocumentTypeDto>> GetDeletedAsync()
    {
        return _documentTypeAppService.GetDeletedAsync();
    }

    [HttpPost]
    public virtual Task<DocumentTypeDto> CreateAsync([FromBody] CreateDocumentTypeDto input)
    {
        return _documentTypeAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    public virtual Task<DocumentTypeDto> UpdateAsync(Guid id, [FromBody] UpdateDocumentTypeDto input)
    {
        return _documentTypeAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _documentTypeAppService.DeleteAsync(id);
    }

    [HttpPost("{id}/restore")]
    public virtual Task<DocumentTypeDto> RestoreAsync(Guid id)
    {
        return _documentTypeAppService.RestoreAsync(id);
    }
}

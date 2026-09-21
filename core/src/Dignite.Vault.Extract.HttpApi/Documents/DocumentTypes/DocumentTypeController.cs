using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.DocumentTypes;

[Area("vault-extract")]
[Route("api/vault-extract/document-types")]
public class DocumentTypeController : VaultExtractController, IDocumentTypeAppService
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

    // #636: an in-process use case (the MCP adapters), not part of the REST contract. Explicit interface
    // implementation satisfies IDocumentTypeAppService without adding a public action method — the conventional
    // C# way to keep an interface member off a plain ASP.NET controller's routable, Swagger-visible surface.
    Task<List<DocumentTypeSummaryDto>> IDocumentTypeAppService.GetVisibleSummariesAsync()
    {
        return _documentTypeAppService.GetVisibleSummariesAsync();
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

    /// <summary>
    /// #651 §5: the pre-save preview for a duplicate-scope switch. A GET with the prospective scope in the query
    /// string — it reads two counts and changes nothing, so a POST would misdescribe it.
    /// </summary>
    [HttpGet("{id}/duplicate-scope-preview")]
    public virtual Task<DuplicateScopePreviewDto> GetDuplicateScopePreviewAsync(
        Guid id, [FromQuery] DuplicateDetectionScope duplicateScope)
    {
        return _documentTypeAppService.GetDuplicateScopePreviewAsync(id, duplicateScope);
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

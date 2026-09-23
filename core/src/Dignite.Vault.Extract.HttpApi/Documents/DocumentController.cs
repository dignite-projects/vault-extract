using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.HttpApi.Documents;

[Area("vault-extract")]
[Route("api/vault-extract/documents")]
public class DocumentController : VaultExtractController, IDocumentAppService
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentController(IDocumentAppService documentAppService)
    {
        _documentAppService = documentAppService;
    }

    [HttpGet("{id}")]
    public virtual Task<DocumentDto> GetAsync(Guid id)
    {
        return _documentAppService.GetAsync(id);
    }

    // #636: an in-process use case (the MCP adapters), not part of the REST contract. Explicit interface
    // implementation satisfies IDocumentAppService without adding a public action method — the conventional C#
    // way to keep an interface member off a plain ASP.NET controller's routable, Swagger-visible surface.
    Task<DocumentDto?> IDocumentAppService.FindForCallerAsync(Guid id)
    {
        return _documentAppService.FindForCallerAsync(id);
    }

    [HttpGet]
    public virtual Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input)
    {
        return _documentAppService.GetListAsync(input);
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public virtual Task<DocumentDto> UploadAsync(UploadDocumentInput input)
    {
        return _documentAppService.UploadAsync(input);
    }

    [HttpGet("{id}/blob")]
    public virtual Task<IRemoteStreamContent> GetBlobAsync(Guid id)
    {
        return _documentAppService.GetBlobAsync(id);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _documentAppService.DeleteAsync(id);
    }

    [HttpDelete("{id}/permanent")]
    public virtual Task PermanentDeleteAsync(Guid id)
    {
        return _documentAppService.PermanentDeleteAsync(id);
    }

    [HttpPost("{id}/restore")]
    public virtual Task RestoreAsync(Guid id)
    {
        return _documentAppService.RestoreAsync(id);
    }

    [HttpPost("{id}/confirm-classification")]
    public virtual Task<DocumentDto> ConfirmClassificationAsync(Guid id, [FromBody] ConfirmClassificationInput input)
    {
        return _documentAppService.ConfirmClassificationAsync(id, input);
    }

    [HttpPost("{id}/reclassify")]
    public virtual Task<DocumentDto> ReclassifyAsync(Guid id, [FromBody] ReclassifyDocumentInput input)
    {
        return _documentAppService.ReclassifyAsync(id, input);
    }

    [HttpPost("{id}/review/reject")]
    public virtual Task<DocumentDto> RejectReviewAsync(Guid id, [FromBody] RejectReviewInput input)
    {
        return _documentAppService.RejectReviewAsync(id, input);
    }

    [HttpPost("{id}/review/allow-duplicate")]
    public virtual Task<DocumentDto> AllowDuplicateAsync(Guid id)
    {
        return _documentAppService.AllowDuplicateAsync(id);
    }

    [HttpPost("{id}/review/resolve-field-validation-warnings")]
    public virtual Task<DocumentDto> ResolveFieldValidationWarningsAsync(
        Guid id, [FromBody] ResolveFieldValidationWarningsInput input)
    {
        return _documentAppService.ResolveFieldValidationWarningsAsync(id, input);
    }

    [HttpPost("{id}/review/confirm-field-entry")]
    public virtual Task<DocumentDto> ConfirmFieldEntryAsync(Guid id)
    {
        return _documentAppService.ConfirmFieldEntryAsync(id);
    }

    [HttpPost("{id}/retry-pipeline")]
    public virtual Task RetryPipelineAsync(Guid id, [FromBody] RetryPipelineInput input)
    {
        return _documentAppService.RetryPipelineAsync(id, input);
    }

    [HttpPost("{id}/reparse")]
    public virtual Task ReparseAsync(Guid id)
    {
        return _documentAppService.ReparseAsync(id);
    }

    [HttpPost("{id}/reextract-fields")]
    public virtual Task ReextractFieldsAsync(Guid id)
    {
        return _documentAppService.ReextractFieldsAsync(id);
    }

    [HttpPost("{id}/extracted-fields")]
    public virtual Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, [FromBody] UpdateExtractedFieldsInput input)
    {
        return _documentAppService.UpdateExtractedFieldsAsync(id, input);
    }

    [HttpPost("{id}/markdown")]
    public virtual Task<DocumentDto> UpdateMarkdownAsync(Guid id, [FromBody] UpdateMarkdownInput input)
    {
        return _documentAppService.UpdateMarkdownAsync(id, input);
    }

    [HttpPost("{id}/cabinet")]
    public virtual Task<DocumentDto> UpdateCabinetAsync(Guid id, [FromBody] UpdateDocumentCabinetInput input)
    {
        return _documentAppService.UpdateCabinetAsync(id, input);
    }
}

using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Exports;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.HttpApi.Documents.Exports;

[Area("vault-extract")]
[Route("api/vault-extract/documents")]
public class DocumentExportController : VaultExtractController, IDocumentExportAppService
{
    private readonly IDocumentExportAppService _documentExportAppService;

    public DocumentExportController(IDocumentExportAppService documentExportAppService)
    {
        _documentExportAppService = documentExportAppService;
    }

    [HttpPost("export")]
    public virtual Task<IRemoteStreamContent> ExportAsync([FromBody] ExportDocumentsInput input)
    {
        return _documentExportAppService.ExportAsync(input);
    }
}

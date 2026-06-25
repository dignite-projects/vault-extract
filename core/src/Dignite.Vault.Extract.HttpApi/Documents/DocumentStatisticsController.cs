using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents;

[Area("vault-extract")]
[Route("api/vault-extract/document-statistics")]
public class DocumentStatisticsController : ExtractController, IDocumentStatisticsAppService
{
    private readonly IDocumentStatisticsAppService _documentStatisticsAppService;

    public DocumentStatisticsController(IDocumentStatisticsAppService documentStatisticsAppService)
    {
        _documentStatisticsAppService = documentStatisticsAppService;
    }

    [HttpGet]
    public virtual Task<DocumentStatisticsDto> GetAsync()
    {
        return _documentStatisticsAppService.GetAsync();
    }
}

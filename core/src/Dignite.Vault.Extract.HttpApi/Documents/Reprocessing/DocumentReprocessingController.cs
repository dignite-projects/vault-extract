using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Reprocessing;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.Reprocessing;

// Handwritten controller that explicitly exposes IDocumentReprocessingAppService. #216: host Auto API
// only covers the host assembly, so AppServices from the Application assembly rely on explicit HttpApi
// controller forwarding; otherwise frontend calls hit 404.
[Area("extract")]
[Route("api/extract/document-reprocessing")]
public class DocumentReprocessingController : ExtractController, IDocumentReprocessingAppService
{
    private readonly IDocumentReprocessingAppService _appService;

    public DocumentReprocessingController(IDocumentReprocessingAppService appService)
    {
        _appService = appService;
    }

    // GET /api/extract/document-reprocessing/field-extraction/preview?documentTypeId=...
    [HttpGet("field-extraction/preview")]
    public virtual Task<FieldReextractionPreviewDto> PreviewFieldExtractionAsync(Guid documentTypeId)
    {
        return _appService.PreviewFieldExtractionAsync(documentTypeId);
    }

    // POST /api/extract/document-reprocessing/field-extraction
    [HttpPost("field-extraction")]
    public virtual Task<ReprocessingStartResultDto> StartFieldExtractionAsync([FromBody] StartFieldReextractionInput input)
    {
        return _appService.StartFieldExtractionAsync(input);
    }

    // POST /api/extract/document-reprocessing/reclassification/preview
    // Uses POST to carry the scope object, including enums, nullable values, and switches, avoiding
    // complex query-string input.
    [HttpPost("reclassification/preview")]
    public virtual Task<ReclassificationPreviewDto> PreviewReclassificationAsync([FromBody] ReclassificationScopeInput input)
    {
        return _appService.PreviewReclassificationAsync(input);
    }

    // POST /api/extract/document-reprocessing/reclassification
    [HttpPost("reclassification")]
    public virtual Task<ReprocessingStartResultDto> StartReclassificationAsync([FromBody] ReclassificationScopeInput input)
    {
        return _appService.StartReclassificationAsync(input);
    }
}

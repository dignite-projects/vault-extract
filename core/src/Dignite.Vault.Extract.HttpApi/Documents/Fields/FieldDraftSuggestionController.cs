using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.Fields;

[Area("extract")]
[Route("api/extract/field-draft-suggestion")]
public class FieldDraftSuggestionController : ExtractController, IFieldDraftSuggestionAppService
{
    private readonly IFieldDraftSuggestionAppService _fieldDraftSuggestionAppService;

    public FieldDraftSuggestionController(IFieldDraftSuggestionAppService fieldDraftSuggestionAppService)
    {
        _fieldDraftSuggestionAppService = fieldDraftSuggestionAppService;
    }

    [HttpPost("draft")]
    public virtual Task<FieldDefinitionDraftDto> DraftAsync([FromBody] DraftFieldDefinitionInput input, CancellationToken cancellationToken)
    {
        return _fieldDraftSuggestionAppService.DraftAsync(input, cancellationToken);
    }
}

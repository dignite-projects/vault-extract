using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Paperbase.HttpApi.Documents.Fields;

[Area("paperbase")]
[Route("api/paperbase/field-draft-suggestion")]
public class FieldDraftSuggestionController : PaperbaseController, IFieldDraftSuggestionAppService
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

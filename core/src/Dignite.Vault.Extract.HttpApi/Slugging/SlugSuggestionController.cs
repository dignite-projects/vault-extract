using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Slugging;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Slugging;

[Area("extract")]
[Route("api/extract/slug-suggestion")]
public class SlugSuggestionController : ExtractController, ISlugSuggestionAppService
{
    private readonly ISlugSuggestionAppService _slugSuggestionAppService;

    public SlugSuggestionController(ISlugSuggestionAppService slugSuggestionAppService)
    {
        _slugSuggestionAppService = slugSuggestionAppService;
    }

    [HttpPost("suggest")]
    public virtual Task<SlugSuggestionDto> SuggestAsync([FromBody] SuggestSlugInput input, CancellationToken cancellationToken)
    {
        return _slugSuggestionAppService.SuggestAsync(input, cancellationToken);
    }
}

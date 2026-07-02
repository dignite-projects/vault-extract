using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.Fields;

[Area("vault-extract")]
[Route("api/vault-extract/field-prompt-polish")]
public class FieldPromptPolishController : VaultExtractController, IFieldPromptPolishAppService
{
    private readonly IFieldPromptPolishAppService _fieldPromptPolishAppService;

    public FieldPromptPolishController(IFieldPromptPolishAppService fieldPromptPolishAppService)
    {
        _fieldPromptPolishAppService = fieldPromptPolishAppService;
    }

    [HttpPost("polish")]
    public virtual Task<FieldPromptPolishResultDto> PolishAsync([FromBody] FieldPromptPolishInput input, CancellationToken cancellationToken)
    {
        return _fieldPromptPolishAppService.PolishAsync(input, cancellationToken);
    }
}

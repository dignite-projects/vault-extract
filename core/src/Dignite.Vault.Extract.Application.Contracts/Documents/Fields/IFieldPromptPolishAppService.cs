using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// AI-polish for a field-extraction prompt (#447): one LLM call that rewrites the administrator's raw
/// instruction into clean, well-formed Markdown, meaning-preserving. Follows the same interactive-LLM
/// safety rules as <see cref="IFieldDraftSuggestionAppService"/> (compile-time constant instructions, input
/// wrapped with <c>PromptBoundary</c>, fail-closed Create/Update permission, untrusted-output sanitize,
/// fail-open on provider failure).
/// </summary>
public interface IFieldPromptPolishAppService : IApplicationService
{
    Task<FieldPromptPolishResultDto> PolishAsync(FieldPromptPolishInput input, CancellationToken cancellationToken = default);
}

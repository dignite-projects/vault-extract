using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Service for "draft field metadata from prompt" (issue #264). Admin provides extraction
/// instructions as the primary input; this service uses one LLM call to <b>draft</b> DisplayName /
/// DataType / IsRequired / AllowMultiple, plus Name for new fields, then the admin reviews / edits
/// before saving.
/// <para>
/// Like <see cref="Dignite.Vault.Extract.Slugging.ISlugSuggestionAppService"/>, this is an interactive
/// request/response LLM drafting helper and reuses the same safety rules (CLAUDE.md "Security
/// Covenant" / .claude/rules/llm-call-anti-patterns.md): compile-time constant instructions, user
/// prompt wrapped with <c>PromptBoundary</c>, no AIContextProviders, and no trust in LLM output
/// (server-side sanitize).
/// </para>
/// </summary>
public interface IFieldDraftSuggestionAppService : IApplicationService
{
    Task<FieldDefinitionDraftDto> DraftAsync(DraftFieldDefinitionInput input, CancellationToken cancellationToken = default);
}

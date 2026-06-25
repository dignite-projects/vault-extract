using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Suggests a display-name-to-machine-identifier slug (issue #190).
/// <para>
/// Reduces the admin burden of filling two fields, display name plus machine key: the admin enters only
/// the display name as the label, and the frontend calls this service so the LLM can produce an English
/// candidate for pre-filling <see cref="FieldDefinition.Name"/> or <see cref="DocumentType.TypeCode"/>.
/// The admin may override it manually.
/// </para>
/// <para>
/// The FieldDefinition and DocumentType creation forms share this single endpoint, and the slug format
/// satisfies both allowlists.
/// </para>
/// </summary>
public interface ISlugSuggestionAppService : IApplicationService
{
    Task<SlugSuggestionDto> SuggestAsync(SuggestSlugInput input, CancellationToken cancellationToken = default);
}

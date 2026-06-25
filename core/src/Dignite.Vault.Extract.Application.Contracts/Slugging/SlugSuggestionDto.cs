namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Output: server-sanitized snake_case machine identifier suggestion.
/// <para>
/// <see cref="Slug"/> is constrained to <c>[a-z0-9_]</c>: lowercase, underscore-separated, and at
/// most 64 characters. It satisfies both <see cref="FieldDefinition.Name"/>
/// (<c>FieldDefinitionConsts.NamePattern</c>) and <see cref="DocumentType.TypeCode"/>
/// (<c>DocumentTypeConsts.TypeCodePattern</c> single-segment shape), so one suggestion is shared by
/// both forms.
/// </para>
/// <para>
/// May be an <b>empty string</b> when the LLM is unavailable, returns non-JSON, or leaves no valid
/// characters after sanitization, such as untranslated pure CJK. The frontend then falls back to local
/// placeholders (<c>field_{n}</c> / <c>type_{n}</c>).
/// </para>
/// </summary>
public class SlugSuggestionDto
{
    public string Slug { get; set; } = string.Empty;
}

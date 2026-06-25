using System.Text;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Fallback-normalizes arbitrary raw text into a <c>[a-z0-9_]</c> snake_case slug: the safety boundary
/// for <b>not trusting LLM output</b>.
/// <para>
/// Shared by <see cref="SlugSuggestionAppService"/> (DisplayName to slug, #190) and
/// <see cref="Dignite.Vault.Extract.Documents.Fields.FieldDraftSuggestionAppService"/> (Name suggested
/// by prompt drafting, #264). The derivation / sanitize logic for Name/TypeCode machine keys is
/// maintained in one place, avoiding drift where one path lets illegal characters through.
/// </para>
/// </summary>
internal static class SlugNormalizer
{
    /// <summary>
    /// Lowercases, collapses non-<c>[a-z0-9]</c> characters into single underscores, trims leading /
    /// trailing underscores, and truncates to <see cref="FieldDefinitionConsts.MaxNameLength"/> (64,
    /// the stricter per-segment limit across FieldDefinition.Name and DocumentType.TypeCode
    /// whitelists). Empty input or input with no valid characters after sanitization, such as
    /// untranslated pure CJK, returns an empty string so callers can fall back to a local placeholder.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else
            {
                // Spaces / hyphens / punctuation / CJK and similar characters all collapse to an
                // underscore placeholder, then the next step merges repeated underscores.
                sb.Append('_');
            }
        }

        var collapsed = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (collapsed.Length > FieldDefinitionConsts.MaxNameLength)
        {
            collapsed = collapsed[..FieldDefinitionConsts.MaxNameLength].Trim('_');
        }

        return collapsed;
    }
}

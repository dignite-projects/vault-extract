using System.Text.RegularExpressions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Whitelist validator for language tags (ISO 639-1 / IETF tags, such as <c>en</c> /
/// <c>zh-Hans</c> / <c>ja</c>).
/// <para>
/// <c>Document.Language</c> is exposed as a <b>raw value</b> in MCP resource metadata headers, without
/// <see cref="Ai.PromptBoundary"/> wrapping, and is interpolated into the language clause of internal
/// LLM system prompts. Like <c>DocumentTypeConsts.TypeCodePattern</c>, the whitelist is the injection
/// defense: only <see cref="Pattern"/> passes, and non-matching candidates are discarded as "language
/// not detected" instead of being truncated or repaired.
/// </para>
/// </summary>
public static class LanguageTagValidator
{
    /// <summary>
    /// Legal language-tag whitelist: ASCII letters / digits / hyphen, 1 to 16 characters, with the
    /// upper bound aligned with the default <see cref="DocumentConsts.MaxLanguageLength"/>. Compile-time
    /// <c>const</c>: this safety boundary must not be widened by runtime configuration, matching the
    /// <see cref="DocumentConsts.MaxSearchResultCount"/> pattern.
    /// </summary>
    public const string Pattern = "^[A-Za-z0-9-]{1,16}$";

    private static readonly Regex TagRegex = new(
        Pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Trims first, then validates against the <see cref="Pattern"/> whitelist.
    /// null / blank / non-matching values, including spaces / punctuation / control characters /
    /// overlength values, return <c>null</c>. Callers treat that as "language not detected".
    /// </summary>
    public static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        return TagRegex.IsMatch(trimmed) ? trimmed : null;
    }
}

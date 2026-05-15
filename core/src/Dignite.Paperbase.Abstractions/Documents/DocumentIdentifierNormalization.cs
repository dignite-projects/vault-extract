using System.Text;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Helper class providing two canonical normalization strategies for identifier values.
/// Used by business module providers when producing
/// <see cref="DocumentIdentifierEntry.NormalizedValue"/> and when comparing stored data
/// to incoming normalized lookup values.
///
/// <para>
/// <strong>This is a utility, NOT a contract</strong>. The core layer does NOT decide which
/// strategy applies to which type — that's the provider's responsibility (see
/// <see cref="IDocumentIdentifierProvider"/> docs). Two providers that handle the same
/// identifier type string MUST use a compatible normalization rule (otherwise cross-module
/// matching silently fails). New business modules can call these helpers, implement their
/// own normalization, or both — there's no central registry.
/// </para>
///
/// <para>
/// <strong>Goal</strong>: collapse uncontrolled surface variation (half-width vs full-width,
/// em-dash vs hyphen, casual whitespace, mixed casing) into a single comparison key. Two
/// documents holding the same business identifier ("HT-2024-001" vs "ht2024001" vs
/// "ＨＴ－２０２４－００１") must produce the same normalized value; different business
/// identifiers must produce different normalized values. The normalized form is lossy and
/// intentionally not round-trip-able — it's a key, not a display value (storage keeps the
/// raw too).
/// </para>
/// </summary>
public static class DocumentIdentifierNormalization
{
    /// <summary>
    /// Aggressive normalization for identifier codes: strip every separator (hyphens, slashes,
    /// dots, spaces, brackets, etc.), normalize unicode digit forms to ASCII, uppercase the rest.
    ///
    /// <para>
    /// Examples (input → output):
    /// <list type="bullet">
    /// <item><c>"HT-2024-001"</c> → <c>"HT2024001"</c></item>
    /// <item><c>"ht 2024 001"</c> → <c>"HT2024001"</c></item>
    /// <item><c>"HT—2024—001"</c> (em-dash) → <c>"HT2024001"</c></item>
    /// <item><c>"HT/2024/001"</c> → <c>"HT2024001"</c></item>
    /// <item><c>"ＨＴ－２０２４－００１"</c> (full-width) → <c>"HT2024001"</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static string NormalizeIdentifierCode(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return string.Empty;

        // Step 1: Unicode-normalize to compatibility form — folds full-width digits/letters
        // ("ＨＴ" / "２０２４") into their ASCII equivalents, collapses ligatures, etc.
        var compatibility = rawValue.Normalize(NormalizationForm.FormKC);

        // Step 2: drop everything that isn't a letter or digit. This wipes hyphens, slashes,
        // spaces, dots, brackets, both ASCII and full-width punctuation, and the various
        // dash characters (en-dash U+2013, em-dash U+2014, Chinese fullwidth dash U+FF0D
        // are non-alphanumeric so they're stripped). Uppercase ASCII letters.
        var sb = new StringBuilder(compatibility.Length);
        foreach (var ch in compatibility)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Conservative normalization for entity (party / company) names: collapse whitespace,
    /// normalize Unicode form, trim. Preserves casing and structural punctuation because
    /// company-name structure matters for matching ("XYZ Co., Ltd." vs "XYZ Co Ltd" should
    /// not collide with "XYZ Co. (HK), Ltd.").
    ///
    /// <para>
    /// Examples (input → output):
    /// <list type="bullet">
    /// <item><c>"  上海某某  科技  有限公司  "</c> → <c>"上海某某 科技 有限公司"</c></item>
    /// <item><c>"XYZ　(国际)　Ltd."</c> (full-width space) → <c>"XYZ (国际) Ltd."</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static string NormalizeEntityName(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return string.Empty;

        var compatibility = rawValue.Normalize(NormalizationForm.FormKC);

        // Collapse any run of whitespace (including U+3000, U+00A0) into a single ASCII space.
        var sb = new StringBuilder(compatibility.Length);
        var lastWasSpace = false;
        foreach (var ch in compatibility)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || lastWasSpace) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        // Trim trailing single space if any.
        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }
}

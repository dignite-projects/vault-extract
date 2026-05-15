using System;
using System.Globalization;
using System.Text;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// L2 RelationDiscovery hardening — value normalization (硬伤一).
///
/// <para>
/// Identifier values arrive from LLM extraction of OCR'd / digital documents and carry
/// uncontrolled variation: half-width vs full-width characters, en-dash vs em-dash vs
/// hyphen, casual whitespace, mixed casing. Two documents holding the SAME business
/// identifier ("HT-2024-001" vs "ht2024001") would fail `==` matching and the relationship
/// would silently disappear. This class collapses the surface noise into a comparable
/// canonical form **without changing semantics**.
/// </para>
///
/// <para>
/// <strong>Contract</strong>: same business identifier MUST produce the same normalized value;
/// different business identifiers MUST produce different normalized values. We accept that
/// the canonical form may not round-trip to the original (it's lossy by design) — it is a
/// comparison key, not a display value. Storage layers keep the raw value too.
/// </para>
/// </summary>
public static class DocumentIdentifierNormalization
{
    /// <summary>
    /// Returns the normalized form of <paramref name="rawValue"/> for use as an equality key
    /// when comparing identifiers of the given <paramref name="identifierType"/>. Empty input
    /// returns empty; never returns null.
    ///
    /// <para>
    /// Strategy dispatches by <paramref name="identifierType"/> against the well-known constants
    /// in <see cref="DocumentIdentifierTypes"/>. Module-private types (with module-prefixed names,
    /// e.g. <c>"Contracts.SerialCode"</c>) fall through to the same code-style normalizer used
    /// for the standard identifier-number types — modules that need different semantics should
    /// normalize inside their own provider before emitting.
    /// </para>
    /// </summary>
    public static string Normalize(string identifierType, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return string.Empty;

        return identifierType switch
        {
            // Identifier-code types (contract / invoice / PO / project codes): high noise from
            // human transcription. Aggressive normalization: drop ALL separators, uppercase.
            DocumentIdentifierTypes.ContractNumber => NormalizeIdentifierCode(rawValue),
            DocumentIdentifierTypes.InvoiceNumber => NormalizeIdentifierCode(rawValue),
            DocumentIdentifierTypes.PoNumber => NormalizeIdentifierCode(rawValue),
            DocumentIdentifierTypes.ProjectCode => NormalizeIdentifierCode(rawValue),

            // Name types (legal entities, parties): structure is meaningful (suffix "Co., Ltd.",
            // bracketed regions like "上海某某 (国际) 有限公司"), don't strip separators. Casing
            // matters less because Chinese company names dominate the dataset.
            DocumentIdentifierTypes.PartyName => NormalizeEntityName(rawValue),

            // Unknown / module-private types: default to identifier-code normalization. Modules
            // that need different rules should normalize before emitting from their provider.
            _ => NormalizeIdentifierCode(rawValue),
        };
    }

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

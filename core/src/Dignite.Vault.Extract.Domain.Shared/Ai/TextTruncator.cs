namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Truncation for text that is about to cross an LLM boundary — into a prompt, or out to an MCP client.
///
/// <para>
/// A UTF-16 string may not be cut at an arbitrary index: a code point outside the BMP occupies a surrogate
/// pair, and a cut landing between the two halves leaves a lone high surrogate. That is not a valid encoding
/// of any character, and what happens to it downstream (replaced with U+FFFD, rejected by the serializer,
/// silently reinterpreted) is provider- and serializer-specific. Every truncation on an LLM-facing path must
/// therefore go through <see cref="AtCharBoundary"/> rather than a raw range slice.
/// </para>
///
/// <para>
/// This class only makes a cut safe; it does not decide whether cutting is appropriate. Where a missing tail
/// would silently corrupt the result — field extraction, where a type-bound field may sit anywhere in the
/// document — the correct answer is to refuse the call, not to truncate. See
/// <c>VaultExtractBehaviorOptions.MaxFieldExtractionMarkdownLength</c>.
/// </para>
/// </summary>
public static class TextTruncator
{
    /// <summary>
    /// Returns the leading <paramref name="maxChars"/> UTF-16 code units of <paramref name="text"/>, backing off by one
    /// when that boundary would split a surrogate pair. Returns <paramref name="text"/> unchanged when it already fits,
    /// and <see cref="string.Empty"/> when <paramref name="maxChars"/> is not positive.
    /// </summary>
    public static string AtCharBoundary(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end];
    }
}

namespace Dignite.Vault.Extract.Slugging;

public static class SlugSuggestionConsts
{
    /// <summary>
    /// Maximum length for slug suggestion input, the human-readable label. This is the slug service's
    /// own input guardrail, used only to prevent oversized text from being injected into the LLM prompt.
    /// It is independent from any entity column length. The slug service is a generic label-to-machine-key
    /// converter and should not reuse a concrete entity's <c>DisplayName</c> limit.
    /// The value 128 comfortably covers the display names of the two current creation forms.
    /// </summary>
    public static int MaxLabelLength { get; set; } = 128;
}

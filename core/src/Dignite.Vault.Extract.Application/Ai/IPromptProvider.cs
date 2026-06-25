namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Provides system prompts for MAF workflows.
/// Implementations may return different templates by language, tenant, or business scenario. Tests
/// inject substitute implementations to isolate LLM calls.
/// </summary>
public interface IPromptProvider
{
    PromptTemplate GetClassificationPrompt(string language);

    /// <summary>
    /// Born-digital container segmentation prompt (#346). Asks the LLM to return, for each constituent document,
    /// a <b>verbatim</b> start marker plus whether the slice is itself a document — never regenerated content.
    /// The <paramref name="language"/> only governs any incidental wording; the markers must be copied exactly
    /// from the document regardless of language.
    /// </summary>
    PromptTemplate GetSegmentationPrompt(string language);

    /// <summary>
    /// Title generation prompt. It <b>does not</b> accept a language parameter because the title
    /// strategy is "follow the document language"; the prompt includes "Respond in the same language
    /// as the document." It is not affected by <c>ExtractBehaviorOptions.DefaultLanguage</c>.
    /// </summary>
    PromptTemplate GetTitleGenerationPrompt();
}

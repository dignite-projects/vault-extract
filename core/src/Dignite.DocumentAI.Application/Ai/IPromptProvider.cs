namespace Dignite.DocumentAI.Ai;

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
    /// Scenario B figure-document gate prompt (#365). Unlike <see cref="GetClassificationPrompt"/> — which
    /// assumes its input already <b>is</b> a document and only assigns a type — this asks the binary question that
    /// actually gates figure routing: is the figure's transcription a <b>self-contained, standalone document</b>,
    /// or merely an <b>element of its parent</b> (a chart, logo, stamp, photo, diagram, or decorative crop)? It is
    /// conservative by design (modeled on the <c>isContainer</c> reject-list) and parent-aware: the parent's title
    /// and type are supplied so independence is judged against the parent, not in a vacuum. The
    /// <paramref name="language"/> governs the response language only.
    /// </summary>
    PromptTemplate GetFigureGatePrompt(string language);

    /// <summary>
    /// Title generation prompt. It <b>does not</b> accept a language parameter because the title
    /// strategy is "follow the document language"; the prompt includes "Respond in the same language
    /// as the document." It is not affected by <c>DocumentAIBehaviorOptions.DefaultLanguage</c>.
    /// </summary>
    PromptTemplate GetTitleGenerationPrompt();
}

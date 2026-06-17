using Dignite.DocumentAI.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.DocumentAI.Ai;

/// <summary>
/// Built-in <see cref="IPromptProvider"/> implementation.
/// The classification prompt embeds language instructions into the system prompt from the language
/// parameter. The title prompt follows the document language and accepts no language parameter.
/// Returned <see cref="PromptTemplate.SystemInstructions"/> values do not include PromptBoundary
/// rules; each Workflow appends them before use.
/// </summary>
public class DefaultPromptProvider : IPromptProvider, ITransientDependency
{
    /// <summary>
    /// Fallback value for the language clause when the language argument is invalid. Kept aligned with
    /// the default <see cref="DocumentAIBehaviorOptions.DefaultLanguage"/>, which is the value hosts
    /// already get when unconfigured. Compile-time constant; no runtime string concatenation.
    /// </summary>
    private const string FallbackLanguage = "ja";

    public virtual PromptTemplate GetClassificationPrompt(string language) => new(
        "You are a document classification expert. " +
        "Analyze the document text and determine the best matching document type from the provided list. " +
        "The document content is provided as Markdown — treat headings (#), tables, and lists as semantic " +
        "structure signals (e.g. an invoice usually has a table of line items; a contract has numbered clauses). " +
        "Return JSON only. Confidence values must be decimal scores from 0.0 to 1.0; never return percentages. " +
        "If you are not confident, set confidence low and typeCode to null. " +
        // #346: container detection rides this same classification call. Keep it CONSERVATIVE — only a clear
        // bundle of several independent documents qualifies; attachments / annexes / continuation pages / a
        // single ledger must never be flagged, or normal documents would stop extracting.
        "Set isContainer to true ONLY when the content is clearly several independent, complete documents bundled " +
        "into one file — for example many separate invoices in one file, or a pack mixing a contract plus an " +
        "invoice plus a receipt. When isContainer is true the type guess is not used, so still fill typeCode and " +
        "confidence with your best guess but they will be ignored. " +
        "Do NOT set isContainer for any of these (they are each a single document): a document with attachments, " +
        "annexes, 別紙, appendices, or exhibits; a multi-page single document such as a continuation page or " +
        "line-item overflow of one invoice or contract (the same document identity / header repeats); or a single " +
        "register, ledger, or itemized table that merely lists many rows. When in doubt, set isContainer to false. " +
        // Defensive validation: language comes from host trust-domain configuration
        // (DocumentAIBehaviorOptions.DefaultLanguage), so it does not violate the "compile-time
        // constants for instructions" safety rule. Still, before interpolation into the system prompt,
        // it passes through the LanguageTagValidator whitelist, the same defense as Document.SetLanguage.
        // If configuration accidentally contains a full sentence / multiline text, fall back to the
        // default value and preserve the "Respond in: <tag>." clause shape and semantics.
        $"Respond in: {LanguageTagValidator.Normalize(language) ?? FallbackLanguage}."
    );

    public virtual PromptTemplate GetTitleGenerationPrompt() => new(
        "You generate concise document titles. " +
        "Given a document in Markdown format, return one short descriptive title only — " +
        "the kind that appears in a file browser or search result. " +
        "Do not wrap it in quotes. Do not add surrounding punctuation. " +
        "If the document has an explicit title heading, use it verbatim. " +
        "Otherwise summarize the document's subject in under 80 characters. " +
        "Respond in the same language as the document."
    );
}

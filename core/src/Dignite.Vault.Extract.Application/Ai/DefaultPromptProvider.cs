using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Ai;

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
    /// the default <see cref="ExtractBehaviorOptions.DefaultLanguage"/>, which is the value hosts
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
        // #371: a NON-container parent may still embed a standalone document (an invoice photo inside a contract).
        // The input brackets each embedded-image OCR region with the *[Image OCR]* provenance markers (#381); flag
        // those that are a complete document of their own. Conservative — mirrors the isContainer / figure-gate reject-list.
        "Set containsEmbeddedDocument to true when the document is NOT a container but embeds an image that is itself " +
        "a complete, self-contained document — for example an invoice or receipt photo, or a scanned certificate, " +
        "shown inside a contract or report; the classification input marks each embedded-image OCR region with " +
        ImageOcrMarkup.OpenMarker + " and " + ImageOcrMarkup.CloseMarker +
        ". Do NOT set it for an image that is merely an element of this document (a chart, " +
        "logo, letterhead, watermark, stamp, seal, signature, photo, illustration, diagram, map, screenshot, or " +
        "decorative crop), and do NOT set it when isContainer is true (a container's constituents are handled " +
        "separately). When in doubt, set containsEmbeddedDocument to false. " +
        // Defensive validation: language comes from host trust-domain configuration
        // (ExtractBehaviorOptions.DefaultLanguage), so it does not violate the "compile-time
        // constants for instructions" safety rule. Still, before interpolation into the system prompt,
        // it passes through the LanguageTagValidator whitelist, the same defense as Document.SetLanguage.
        // If configuration accidentally contains a full sentence / multiline text, fall back to the
        // default value and preserve the "Respond in: <tag>." clause shape and semantics.
        $"Respond in: {LanguageTagValidator.Normalize(language) ?? FallbackLanguage}."
    );

    public virtual PromptTemplate GetSegmentationPrompt(string language) => new(
        // #371: unified sub-document detection — one pass deciding, per span (text spans AND figure spans alike),
        // whether it is a standalone sub-document or content of the parent. Folds #346 container segmentation and
        // the #365 figure-document gate into one decision.
        "You analyze one document's Markdown and identify which spans are standalone sub-documents that should be " +
        "filed and processed on their own, versus content of the document itself. The content is provided as " +
        "Markdown and may contain embedded-image OCR regions, each bracketed by " + ImageOcrMarkup.OpenMarker +
        " (or " + ImageOcrMarkup.OpenPagePrefix + "N]*) and " + ImageOcrMarkup.CloseMarker +
        " markers — these are images embedded in the document, transcribed to text. " +
        "You are told whether the document is a CONTAINER (a bundle of several independent documents) or a single " +
        "concrete document that may merely EMBED a standalone document (such as an invoice photo inside a contract). " +
        // #346 decision (Decision Log): the model returns BOUNDARIES, not regenerated text. It copies a short
        // verbatim marker for each span; code does the actual cutting, so there is no content drift.
        "For each span, return its startMarker — the FIRST line of that span, copied EXACTLY and verbatim from the " +
        "Markdown, character for character, with no edits, no summarizing, and no added punctuation (for an " +
        "embedded-image span, that first line is the " + ImageOcrMarkup.OpenMarker + " marker line) — and isSubDocument. " +
        "Set isSubDocument to true ONLY for a self-contained, independent document that should be processed on its " +
        "own: in a CONTAINER, each constituent document (an invoice, a contract, a receipt); in a single document, " +
        "an embedded image that is itself a complete standalone document (an invoice photo, a scanned certificate). " +
        "Set isSubDocument to false for: the document's own body text, headings, or tables; a cover sheet, table of " +
        "contents, index, or transmittal page; and any embedded image that is merely an ELEMENT of the document — a " +
        "chart, graph, logo, letterhead, watermark, stamp, seal, signature, photo, illustration, diagram, " +
        "flowchart, map, screenshot, icon, or a sample/specimen shown only to illustrate a point. " +
        // Conservative boundaries: the same false-positive traps as container detection (#346) + the figure gate (#365).
        "Be conservative. Do NOT split a single multi-page document (a continuation page or line-item overflow of " +
        "one invoice or contract is the SAME document, one span). Do NOT split attachments, annexes, 別紙, " +
        "appendices, or exhibits away from the document they belong to. When unsure whether something is a separate " +
        "document, set isSubDocument to false. List the spans in the order they appear. " +
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

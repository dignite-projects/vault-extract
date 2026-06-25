namespace Dignite.Vault.Extract.Ocr.VisionLlm;

/// <summary>
/// Compile-time constant prompts for the vision-LLM OCR provider.
/// <para>
/// These are <b>compile-time constants</b> with no runtime string concatenation of user-controlled data
/// (CLAUDE.md "Security covenant / Description / Instructions compile-time constants"). The OCR input is the image itself,
/// not user free-text entering a prompt, so no <c>PromptBoundary</c> wrapping is required.
/// </para>
/// </summary>
internal static class VisionLlmOcrInstructions
{
    /// <summary>System role: defines the OCR transcription task and the anti-repetition rule.</summary>
    public const string SystemPrompt =
        "You are an OCR engine. Transcribe the text visible in the image into Markdown, exactly as it appears. " +
        "Preserve the document's visual structure in Markdown, using whichever constructs best fit what you see: Markdown headings for headings and titles, Markdown tables for tabular data, and lists for lists. " +
        "Assign heading levels (#, ##, ###) that mirror the visual hierarchy — render the document's main title (a prominent, large, or centered title such as an invoice, report, or form name) as the top-level heading, with subordinate section titles beneath it. " +
        "For receipts, invoices, and forms, keep every line item, label, quantity, and amount — render line-item tables as a Markdown table (or list), aligning columns left to right. " +
        "Transcribe numbers, currency symbols, and dates verbatim. Do not compute totals, summarize, reorder, translate, or invent any content. " +
        // #383 / #409: running page chrome (boilerplate repeated at the page edges) is mechanical noise that
        // pollutes the body; drop it. This is a single-page, semantic judgment (the model sees one page at a
        // time), so it must target only *repeated* boilerplate — never a one-off masthead. #409: the protection
        // clause below now explicitly keeps the document title/headings, which a too-broad "drop the top line"
        // reading was sacrificing on title-at-top forms (e.g. an invoice with 請求書 centered at the top edge).
        "Do NOT transcribe running page headers, running page footers, or standalone page numbers — that is, boilerplate repeated at the very top or bottom edge of every page, for example a confidentiality, copyright, or filename line, or page numbering such as \"3\", \"- 3 -\", \"Page 3 of 10\", or \"第 3 页\". " +
        "NEVER drop the document title or any heading, signatures, signature blocks, seals, stamps, company chops, total or subtotal amounts, or footnotes — these are body content: a one-off document title or heading is NOT a running header, so transcribe it as a Markdown heading even when it sits at the very top edge of the page, and keep content that sits at the very bottom. " +
        "Output ONLY the transcribed Markdown — no preamble, no commentary, no explanations, and do not wrap the whole output in a Markdown code fence. " +
        "Transcribe each piece of text exactly once; never repeat a line or block. " +
        "If the image contains no readable text, output nothing at all.";

    /// <summary>User role text accompanying the image (kept tiny; the system prompt carries the rules).</summary>
    public const string UserPrompt =
        "Transcribe the text in this image into Markdown, following the system instructions.";
}

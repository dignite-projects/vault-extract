namespace Dignite.Extract.Ocr.VisionLlm;

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
        "Preserve document structure: Markdown headings for headings, Markdown tables for tabular data, and lists for lists. " +
        "For receipts, invoices, and forms, keep every line item, label, quantity, and amount — render line-item tables as a Markdown table (or list), aligning columns left to right. " +
        "Transcribe numbers, currency symbols, and dates verbatim. Do not compute totals, summarize, reorder, translate, or invent any content. " +
        // #383: page chrome is mechanical noise that pollutes the body; drop it. This is a single-page,
        // semantic judgment (the model sees one page at a time) — guarded by the explicit protection list
        // below so body content at the page bottom is never sacrificed.
        "Do NOT transcribe running page headers, running page footers, or standalone page numbers — for example a title or confidentiality/copyright line repeated at the very top or bottom edge of the page, or page numbering such as \"3\", \"- 3 -\", \"Page 3 of 10\", or \"第 3 页\". " +
        "NEVER drop signatures, signature blocks, seals, stamps, company chops, total or subtotal amounts, or footnotes — these are body content and must be transcribed even when they sit at the very bottom of the page. " +
        "Output ONLY the transcribed Markdown — no preamble, no commentary, no explanations, and do not wrap the whole output in a Markdown code fence. " +
        "Transcribe each piece of text exactly once; never repeat a line or block. " +
        "If the image contains no readable text, output nothing at all.";

    /// <summary>User role text accompanying the image (kept tiny; the system prompt carries the rules).</summary>
    public const string UserPrompt =
        "Transcribe the text in this image into Markdown, following the system instructions.";
}

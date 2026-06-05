namespace Dignite.Paperbase.Ocr.VisionLlm;

/// <summary>
/// Compile-time constant prompts for the vision-LLM OCR provider.
/// <para>
/// These are <b>compile-time constants</b> with no runtime string concatenation of user-controlled data
/// (CLAUDE.md "## 安全约定 / Description / Instructions 编译期常量"). The OCR input is the image itself,
/// not user free-text entering a prompt, so no <c>PromptBoundary</c> wrapping is required.
/// </para>
/// </summary>
internal static class VisionLlmOcrInstructions
{
    /// <summary>System role: defines the OCR transcription task and the anti-repetition rule.</summary>
    public const string SystemPrompt =
        "You are an OCR engine. Transcribe ALL text visible in the image into Markdown, exactly as it appears. " +
        "Preserve document structure: Markdown headings for headings, Markdown tables for tabular data, and lists for lists. " +
        "For receipts, invoices, and forms, keep every line item, label, quantity, and amount — render line-item tables as a Markdown table (or list), aligning columns left to right. " +
        "Transcribe numbers, currency symbols, and dates verbatim. Do not compute totals, summarize, reorder, translate, or invent any content. " +
        "Output ONLY the transcribed Markdown — no preamble, no commentary, no explanations, and do not wrap the whole output in a Markdown code fence. " +
        "Transcribe each piece of text exactly once; never repeat a line or block. " +
        "If the image contains no readable text, output nothing at all.";

    /// <summary>User role text accompanying the image (kept tiny; the system prompt carries the rules).</summary>
    public const string UserPrompt =
        "Transcribe the text in this image into Markdown, following the system instructions.";
}

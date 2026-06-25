using System.Collections.Generic;

namespace Dignite.Vault.Extract.Ocr.PaddleOcr;

public class PaddleOcrOptions
{
    /// <summary>PaddleOCR REST service address, defaulting to the local Docker sidecar.</summary>
    public string Endpoint { get; set; } = "http://localhost:8866";

    /// <summary>
    /// Model to use. Three tradeoffs:
    /// <list type="bullet">
    ///   <item><c>PP-StructureV3</c> (default): CPU-capable, outputs Markdown with headings / tables /
    ///   stamps, and is best for Chinese scenarios.</item>
    ///   <item><c>PP-OCRv4</c>: lightest, line-level OCR only, no structured Markdown output. The
    ///   Provider wraps sidecar raw_text into flat Markdown paragraphs to satisfy the Markdown-first
    ///   contract.</item>
    ///   <item><c>PaddleOCR-VL-1.5</c>: high-accuracy VLM, outputs Markdown, requires GPU.</item>
    /// </list>
    /// </summary>
    public string ModelName { get; set; } = "PP-StructureV3";

    /// <summary>Default recognition language list (BCP 47), overridden by OcrOptions.LanguageHints.</summary>
    public IList<string> Languages { get; set; } = new List<string> { "ja", "en" };

    /// <summary>
    /// OCR request timeout in seconds. PP-StructureV3 can take several minutes on CPU for multi-page
    /// image PDFs. Default is 600 seconds (10 minutes). Override in the PaddleOcr section of
    /// appsettings.json.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;
}

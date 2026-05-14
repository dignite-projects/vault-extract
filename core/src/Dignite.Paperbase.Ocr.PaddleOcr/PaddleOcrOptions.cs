using System.Collections.Generic;

namespace Dignite.Paperbase.Ocr.PaddleOcr;

public class PaddleOcrOptions
{
    /// <summary>PaddleOCR REST 服务地址，默认本地 Docker sidecar。</summary>
    public string Endpoint { get; set; } = "http://localhost:8866";

    /// <summary>
    /// 使用的模型。三种取舍：
    /// <list type="bullet">
    ///   <item><c>PP-StructureV3</c>（默认）—— CPU 可用，输出 Markdown（标题/表格/印章），中文场景最佳。</item>
    ///   <item><c>PP-OCRv4</c> —— 最轻量、纯线级 OCR，无结构化 Markdown 输出；Provider 会把 sidecar 的 raw_text 包成扁平 Markdown 段落以符合 Markdown-first 契约。</item>
    ///   <item><c>PaddleOCR-VL-1.5</c> —— VLM 高精度，输出 Markdown，需 GPU。</item>
    /// </list>
    /// </summary>
    public string ModelName { get; set; } = "PP-StructureV3";

    /// <summary>默认识别语言列表（BCP 47），可被 OcrOptions.LanguageHints 覆盖。</summary>
    public IList<string> Languages { get; set; } = new List<string> { "ja", "en" };

    /// <summary>
    /// OCR 请求超时（秒）。PP-StructureV3 在 CPU 上处理多页图片 PDF 可能需要数分钟，
    /// 默认 600 秒（10 分钟）。可在 appsettings.json 的 PaddleOcr 节覆盖。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;
}

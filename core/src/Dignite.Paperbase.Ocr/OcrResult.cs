namespace Dignite.Paperbase.Ocr;

/// <summary>
/// OCR Provider 输出。Markdown-first：实现方<b>必须</b>填充 <see cref="Markdown"/>，
/// 即便底层服务只返回纯文本也需在 Provider 内部包成扁平 Markdown 段落（保持下游消费一种格式）。
/// </summary>
/// <remarks>
/// 当前字段集合刻意精简至 4 个文本相关字段。out-of-band 信号（每页 bbox / 印章位置 /
/// 表单 key-value / page-level metadata）<b>与本类正交</b>——未来若需要 page-aware
/// citations 等能力，应作为本类上具名可选强类型字段（例如 <c>IReadOnlyList&lt;PageBlock&gt;? PageBlocks</c>），
/// <b>禁止</b>塞回 <see cref="Markdown"/> 字符串或通过 <c>Dictionary&lt;string, object&gt;</c> 扩展槽承载。
/// 每加一种 out-of-band 信号应单独开 Issue 讨论。
/// </remarks>
public class OcrResult
{
    /// <summary>
    /// 结构化 Markdown 输出。Provider 未识别到任何内容时为空字符串。
    /// 对结构化文档而言是真信号（标题/表格/列表）；对无结构 OCR 散段落而言只是下游统一的容器命名。
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>整体识别置信度（0.0 ~ 1.0）。</summary>
    public double Confidence { get; set; }

    /// <summary>检测到的主要语言（BCP 47 格式）。</summary>
    public string? DetectedLanguage { get; set; }

    /// <summary>识别的页数。</summary>
    public int PageCount { get; set; }
}

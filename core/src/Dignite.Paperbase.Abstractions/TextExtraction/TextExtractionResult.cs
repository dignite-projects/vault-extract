namespace Dignite.Paperbase.Abstractions.TextExtraction;

public class TextExtractionResult
{
    /// <summary>
    /// 结构化 Markdown 输出。Provider 未识别到任何内容时为空字符串。
    /// 这是 TextExtraction 流水线的<b>唯一</b>文本载荷——下游需要纯文本时通过
    /// <see cref="Dignite.Paperbase.Documents.MarkdownStripper"/> 投影。
    /// </summary>
    /// <remarks>
    /// <see cref="ITextExtractor"/> 实现方<b>必须</b>填充本字段为 Markdown；
    /// 即使源文件无结构（例如低质量 OCR 仅产出散段落），也应以扁平 Markdown 段落输出，
    /// 而<b>不能</b>退回 plain text 或在本类上新增并行的纯文本字段。
    /// <para>
    /// <b>对结构化文档而言</b>，标题、表格、列表是向量化切块与 LLM 理解的真信号；
    /// <b>对无结构内容而言</b>，扁平 Markdown 段落只是<b>容器命名</b>（与 plain text 双换行重组字面相同），
    /// 保留 Markdown 路径是为下游统一消费一种格式，不是信号增益。
    /// </para>
    /// <para>
    /// out-of-band 信号（坐标 / page metadata / 表单 key-value）与本字段正交——未来需要时应作为本类上
    /// 具名可选强类型字段（例如 <c>IReadOnlyList&lt;PageBlock&gt;? PageBlocks</c>），<b>禁止</b>塞回 Markdown 字符串
    /// 或通过 <c>Dictionary&lt;string, object&gt;</c> 扩展槽承载。
    /// </para>
    /// </remarks>
    public string Markdown { get; set; } = string.Empty;

    public double Confidence { get; set; }
    public string? DetectedLanguage { get; set; }
    public int PageCount { get; set; }

    /// <summary>true = OCR (physical scan), false = direct text layer (digital)</summary>
    public bool UsedOcr { get; set; }
}

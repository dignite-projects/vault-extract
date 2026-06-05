namespace Dignite.Paperbase.Ocr;

/// <summary>
/// OCR Provider 输出。Markdown-first：实现方<b>必须</b>填充 <see cref="Markdown"/>，
/// 即便底层服务只返回纯文本也需在 Provider 内部包成扁平 Markdown 段落（保持下游消费一种格式）。
/// </summary>
/// <remarks>
/// out-of-band 信号（每页 bbox / 印章位置 / 表单 key-value / page-level metadata）
/// <b>与文本载荷正交</b>——其<b>原始</b>载体是 <see cref="NativePayloadContent"/> 等三个扁平字段
/// （#210：归档进 blob，不进 DB / 不塞回 <see cref="Markdown"/>）。
/// 未来若需要 page-aware citations 等<b>规范化</b>能力，应作为本类上具名可选强类型字段
/// （例如 <c>IReadOnlyList&lt;PageBlock&gt;? PageBlocks</c>），<b>禁止</b>塞回 <see cref="Markdown"/> 字符串
/// 或通过 <c>Dictionary&lt;string, object&gt;</c> 扩展槽承载。每加一种规范化 out-of-band 信号应单独开 Issue 讨论。
/// </remarks>
public class OcrResult
{
    /// <summary>
    /// 结构化 Markdown 输出。Provider 未识别到任何内容时为空字符串。
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>检测到的主要语言（BCP 47 格式）。</summary>
    public string? DetectedLanguage { get; set; }

    /// <summary>OCR provider family/name for auditability.</summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// 本次识别是否<b>完整</b>（#268）。<c>true</c>（默认）= provider 认为已捕获全部内容；
    /// <c>false</c> = 已知有内容缺失（输出被 token 上限截断、命中重复守卫被丢弃、多页 PDF 有页未能转写等）。
    /// 不支持完整性概念的 provider（PaddleOCR / Azure DI）保持默认 <c>true</c>，行为不变。
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>不完整时（<see cref="IsComplete"/> 为 false）的简短诊断说明；完整时为 <c>null</c>。</summary>
    public string? IncompleteReason { get; set; }

    // === 扁平 native payload（#210）===
    // 原 OcrNativePayload 三字段直接上移，消除 Ocr 项目内的专用包装类。
    // 由 DefaultTextExtractor 映射到 TextExtractionResult.NativePayload（Abstractions.NativePayload）。
    // Provider 无空间模型时三字段均为 null（整体视为无 payload）。

    /// <summary>provider 原生输出的不透明字节（通常是 provider 原始 JSON 响应的 UTF-8 编码）；无 payload 时 null。</summary>
    public byte[]? NativePayloadContent { get; set; }

    /// <summary>payload 的 MIME 类型（如 <c>application/json</c>）；无 payload 时 null。</summary>
    public string? NativePayloadContentType { get; set; }

    /// <summary>schema 标识，供下游消费方判断如何解析（如 <c>PaddleOCR/PP-StructureV3</c>）；无 payload 时 null。</summary>
    public string? NativePayloadSchemaName { get; set; }
}

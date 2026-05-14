using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Abstractions.TextExtraction;

/// <summary>
/// 文本提取能力端口。纯能力——收文件流与上下文，返回提取结果；
/// 不知道 Document 聚合、不访问仓储。
/// 实现：Dignite.Paperbase.TextExtraction
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first 契约</b>：实现方<b>必须</b>在
/// <see cref="TextExtractionResult.Markdown"/> 中返回 Markdown 文本。Markdown 同时被向量化（结构感知切块）、
/// LLM 分类 / QA / Rerank、业务模块字段抽取消费。
/// </para>
/// <para>
/// <b>对结构化文档而言</b>（合同 / 报告 / CSV / 带标题的 DOCX / layout-aware OCR 输出），
/// 标题、表格、列表是 LLM 推理的真信号。
/// <b>对无结构内容而言</b>（OCR 散段落 / 纯 txt / PP-OCRv4 行级输出），扁平 Markdown 段落与纯文本双换行重组字面相同——
/// Markdown 是<b>容器命名</b>而非信号增益，保留此路径只是为了下游消费一种格式。
/// </para>
/// <para>
/// 即使源文件没有结构，仍应以扁平 Markdown 段落输出，而<b>不能</b>退回到独立的"plain text"路径或在
/// <see cref="TextExtractionResult"/> 上引入并行的纯文本字段。下游需要纯文本时，统一通过
/// <c>Dignite.Paperbase.Documents.MarkdownStripper</c> 在消费侧投影。
/// </para>
/// <para>
/// <b>out-of-band 信号</b>（坐标 / page metadata / 表单 key-value）与 Markdown 正交——未来扩展应作为
/// <see cref="TextExtractionResult"/> 上具名可选强类型字段，而非塞回 Markdown 字符串或通用扩展槽。
/// </para>
/// </remarks>
public interface ITextExtractor
{
    /// <summary>
    /// 从文件流中提取 Markdown。
    /// </summary>
    /// <param name="fileStream">原始文件流。</param>
    /// <param name="context">业务无关的提取上下文（contentType / 文件名 / 期望语言等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 包含 <see cref="TextExtractionResult.Markdown"/> 的提取结果。
    /// 未识别到任何内容时 <see cref="TextExtractionResult.Markdown"/> 为空字符串，
    /// 但<b>不应</b>返回 <c>null</c>，也<b>不应</b>抛异常代替"无内容"。
    /// </returns>
    Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default);
}

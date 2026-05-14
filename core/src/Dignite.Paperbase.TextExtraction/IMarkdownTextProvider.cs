using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;

namespace Dignite.Paperbase.TextExtraction;

/// <summary>
/// 数字版文档（PDF/Word/HTML/纯文本/CSV/RTF/EPUB 等）→ Markdown Provider 抽象。
/// 处理具备数字文本层的文件，与处理图像/扫描件的 <c>IOcrProvider</c> 互补。
/// 消费者固定为 <c>DefaultTextExtractor</c>，实现方由独立 Provider 模块
/// （如 <c>Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown</c>）提供，
/// Host 侧通过 <c>DependsOn</c> 选择启用一个实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first 契约</b>：实现方<b>必须</b>把抽取结果填充到
/// <see cref="TextExtractionResult.Markdown"/>，而<b>不能</b>退回 plain text 或新增并行纯文本字段——
/// 任何"plain text fallback"都属于设计违规。
/// </para>
/// <para>
/// <b>对结构化文档而言</b>（带标题的 DOCX / 排版整齐的 PDF / CSV 表格），Markdown 标题、表格、列表是
/// 后续向量化切块（结构感知）与 LLM 理解的真信号——全力利用。
/// <b>对无结构内容而言</b>（裸 txt / 单段 RTF），Markdown 是<b>容器命名</b>而非信号增益，
/// 保留此契约只是为了下游消费一种格式。
/// </para>
/// </remarks>
public interface IMarkdownTextProvider
{
    Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default);
}

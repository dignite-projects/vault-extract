using System.IO;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Ocr;

/// <summary>
/// OCR 服务 Provider 接口——OCR Provider 实现侧的最小契约层。
/// 第三方接入 Paperbase 文本提取流水线只需引用 <c>Dignite.Paperbase.Ocr</c>
/// （拿到 <see cref="IOcrProvider"/> + <see cref="OcrOptions"/> + <see cref="OcrResult"/>），
/// 无需引用 <c>Dignite.Paperbase.TextExtraction</c>（orchestrator）或
/// <c>Dignite.Paperbase.Abstractions</c>（顶层 ITextExtractor 契约）。
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first 契约</b>：实现方<b>必须</b>填充 <see cref="OcrResult.Markdown"/>。
/// 若底层服务输出本身就是 layout-aware Markdown（如 PaddleOCR PP-StructureV3、Azure DI `prebuilt-document`），
/// 直接透传——这种情况下标题、表格、列表是 LLM 理解的真信号。
/// 若底层服务只返回纯文本（如 PaddleOCR PP-OCRv4），Provider <b>自己</b>负责把段落包成扁平 Markdown
/// （例如 <c>string.Join("\n\n", paragraphs)</c>）；<b>不得</b>把翻译职责留给上游 orchestrator。
/// </para>
/// <para>
/// 此扁平包装是<b>为下游统一消费一种格式</b>，对无结构 OCR 输出而言<b>不带来额外信号增益</b>——
/// 不要把它叙述成"扁平段落也是 Markdown 信号"。诚实的视角：Markdown 是文本载荷的统一容器名，
/// 结构化内容才让标记真正"说话"。
/// </para>
/// <para>
/// <b>out-of-band 信号</b>（每页坐标 / bbox / 印章位置 / 表单 key-value）与本契约的 Markdown 字段正交。
/// 当前 <see cref="OcrResult"/> 故意只暴露 4 个文本相关字段。未来若 page-aware citations 等需求落地，
/// 应作为 <see cref="OcrResult"/> 上具名可选强类型字段加入，<b>禁止</b>通过通用 Dictionary 扩展槽。
/// </para>
/// </remarks>
public interface IOcrProvider
{
    Task<OcrResult> RecognizeAsync(Stream fileStream, OcrOptions options);
}

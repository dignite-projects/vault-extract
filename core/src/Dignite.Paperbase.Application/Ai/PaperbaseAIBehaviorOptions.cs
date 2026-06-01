namespace Dignite.Paperbase.Ai;

/// <summary>
/// Application-layer behavior knobs for AI workflows (Classification / structured field extraction).
/// Bound to the <c>PaperbaseAIBehavior</c> configuration section in
/// <see cref="PaperbaseApplicationModule"/>.
/// <para>
/// Provider wiring (endpoint / API key / model ids / prompt-cache middleware) lives in the
/// separate <c>PaperbaseAI</c> section consumed by the host's <c>ConfigureAI</c> — keep these
/// two concerns disjoint: this class must not grow connection or credential fields.
/// </para>
/// </summary>
public class PaperbaseAIBehaviorOptions
{
    /// <summary>
    /// 分类提示词中最多包含的候选类型数量，超出时按 Priority 降序截断。
    /// </summary>
    public int MaxDocumentTypesInClassificationPrompt { get; set; } = 50;

    /// <summary>
    /// 结构化提取单次调用最大文本长度，超出时截断。
    /// </summary>
    public int MaxTextLengthPerExtraction { get; set; } = 8000;

    /// <summary>
    /// AI 交互默认语言。<b>仅作用于分类路径</b>（DocumentClassificationWorkflow，强制分类
    /// 输出/reason 用此语言）。其余 LLM 路径按各自设计的语言策略，<b>不</b>消费此选项：
    /// <list type="bullet">
    ///   <item>分类：强制此语言（DefaultLanguage）</item>
    ///   <item>标题：跟随文档语言（prompt 内置 "respond in the same language as the document"）</item>
    ///   <item>字段值：保留文档原文</item>
    ///   <item>Slug：强制英译（URL 友好）</item>
    /// </list>
    /// 这套「按路径分化」是预期设计，不是 bug——新增 LLM 路径前先确认其语言策略归属。
    /// </summary>
    public string DefaultLanguage { get; set; } = "ja";

    /// <summary>
    /// Title 生成时送入 LLM 的 Markdown 最大字符数。
    /// 超出时截断尾部（文档开头通常已包含标题、摘要等关键信息）。
    /// </summary>
    public int MaxTitleGenerationMarkdownLength { get; set; } = 4000;
}

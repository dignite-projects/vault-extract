namespace Dignite.Paperbase.Ai;

/// <summary>
/// Application-layer behavior knobs for AI workflows (Classification / Embedding / Chat / Rerank).
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
    /// 文本分块大小（字符数），约 400 个日文字符。
    /// </summary>
    public int ChunkSize { get; set; } = 800;

    /// <summary>
    /// 相邻 Chunk 重叠字符数，保证语义连续性。
    /// </summary>
    public int ChunkOverlap { get; set; } = 100;

    /// <summary>
    /// 分块边界回溯容差（字符数）。在 <c>[ChunkSize - ChunkBoundaryTolerance, ChunkSize]</c>
    /// 范围内向后查找最近的自然断点（段落/句末/标点）作为切点，避免硬切句子。
    /// 设为 0 退化为原"固定字符长度"分块。建议值约 ChunkSize 的 15%。
    /// </summary>
    public int ChunkBoundaryTolerance { get; set; } = 120;

    /// <summary>
    /// 结构化提取单次调用最大文本长度，超出时截断。
    /// </summary>
    public int MaxTextLengthPerExtraction { get; set; } = 8000;

    /// <summary>
    /// AI 交互默认语言（影响系统提示词语言）。
    /// </summary>
    public string DefaultLanguage { get; set; } = "ja";

    /// <summary>
    /// 启用 LLM 精排：文档聊天检索先按 <see cref="RecallExpandFactor"/> 扩大召回，
    /// 再让 LLM 对候选 chunk 重新排序，最后只把最终 TopK 注入 prompt。
    /// 默认关闭以保守 token 成本；中文/多语言场景或召回质量不佳时建议启用。
    /// </summary>
    public bool EnableLlmRerank { get; set; } = false;

    /// <summary>
    /// 启用 <see cref="EnableLlmRerank"/> 时的召回扩大倍数。
    /// 实际召回数 = 文档聊天 TopK × 此值。
    /// </summary>
    public int RecallExpandFactor { get; set; } = 4;

    /// <summary>
    /// 文档 Chat RAG 搜索默认最小相关度阈值。模型可在工具参数里显式覆盖（强对账场景拉高、
    /// 跨语言/专有名词查询调低）。低于底层知识库默认值是为了改善跨语言查询和专有名词查询
    /// 的召回。设为 null 时回落到 <c>PaperbaseKnowledgeIndex:MinScore</c>。
    /// </summary>
    public double? DocumentChatMinScore { get; set; } = 0.45;

    /// <summary>
    /// 文档 Chat RAG 搜索默认 TopK。模型可在 <c>search_paperbase_documents</c> 工具参数里
    /// 显式覆盖（如跨文档对账场景拉到 10–15 提升召回宽度）。设为 0 时回落到底层知识库的
    /// <c>DefaultTopK</c>。
    /// </summary>
    public int DocumentChatTopK { get; set; } = 5;

    /// <summary>
    /// Hard upper bound on the number of <see cref="AIFunction"/> tools (built-in
    /// search + every <c>IDocumentChatToolContributor</c> tool) exposed to the LLM
    /// in a single turn. <c>0</c> = unbounded (current default; the production tool
    /// inventory is well below the OpenAI/Azure OpenAI sweet spot of ≤ 10–15 tools
    /// where routing accuracy is high). Activate this cap when the inventory grows
    /// past ~15 tools and routing accuracy degrades. Trimmed contributors are
    /// surfaced via the <c>tools_trimmed</c> telemetry signal so dropouts are
    /// observable, not silent.
    /// </summary>
    public int MaxToolsPerTurn { get; set; } = 0;

    /// <summary>
    /// Title 生成时送入 LLM 的 Markdown 最大字符数。
    /// 超出时截断尾部（文档开头通常已包含标题、摘要等关键信息）。
    /// </summary>
    public int MaxTitleGenerationMarkdownLength { get; set; } = 4000;

    /// <summary>
    /// Document-chat 会话上下文压缩策略。默认关闭，opt-in。详见
    /// <see cref="ChatCompactionOptions"/>。
    /// </summary>
    public ChatCompactionOptions ChatCompaction { get; set; } = new();

    /// <summary>
    /// Hard upper bound on the number of distinct vector-search hits captured per
    /// chat turn (across all <c>search_paperbase_documents</c> invocations). Excess
    /// hits are dropped and a <c>citations_trimmed</c> telemetry signal is recorded.
    /// Bounds prompt-context growth and protects against pathological LLM retry loops.
    /// Set to 0 to disable the cap (not recommended in production).
    /// </summary>
    public int MaxCapturedCitations { get; set; } = 50;
}

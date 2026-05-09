namespace Dignite.Paperbase.Ai;

/// <summary>
/// Document-chat conversation compaction policy. Bound from
/// <c>PaperbaseAIBehavior:ChatCompaction</c>. Default is disabled — opt-in.
///
/// <para>
/// When enabled, <see cref="Dignite.Paperbase.Chat.Compaction.ChatCompactionStrategyFactory"/>
/// builds a layered MAF <c>PipelineCompactionStrategy</c>: tool-result collapse → LLM
/// summarization → sliding window → emergency truncation. Strategies trigger
/// independently against the metrics of the in-flight message list.
/// </para>
/// </summary>
public class ChatCompactionOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>tool-call result collapse trigger (tokens). Pipeline stage 1, gentlest.</summary>
    public int CollapseToolResultsAtTokens { get; set; } = 0x200;   // 512

    /// <summary>summarization trigger (tokens). Pipeline stage 2.</summary>
    public int SummarizeAtTokens { get; set; } = 0x500;             // 1280

    /// <summary>turns retained by sliding window. Pipeline stage 3.</summary>
    public int SlidingWindowTurns { get; set; } = 8;

    /// <summary>truncation backstop (tokens). Pipeline stage 4 — last-resort.</summary>
    public int TruncateAtTokens { get; set; } = 0x8000;             // 32K

    /// <summary>most-recent groups protected from summarization (user/assistant pairs).</summary>
    public int MinimumPreservedGroups { get; set; } = 4;
}

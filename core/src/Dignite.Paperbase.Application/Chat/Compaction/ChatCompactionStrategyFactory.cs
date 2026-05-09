using Dignite.Paperbase.Ai;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

// MAF compaction APIs (CompactionProvider, CompactionStrategy hierarchy, CompactionTriggers)
// are annotated [Experimental(MAAI001)]. This factory is the single integration point;
// suppression is file-scoped here so callers (DocumentChatAppService, tests) don't need it.
#pragma warning disable MAAI001

namespace Dignite.Paperbase.Chat.Compaction;

/// <summary>
/// Builds a MAF <see cref="CompactionProvider"/> from the application's behavior options
/// and the host-registered summarizer <see cref="IChatClient"/>.
///
/// <para>
/// The provider runs once per agent turn (when registered on
/// <c>ChatClientAgentOptions.AIContextProviders</c>): it sees messages already prepended
/// by <c>DocumentChatHistoryProvider</c> and stamped with
/// <c>AgentRequestMessageSourceType.ChatHistory</c>. The pipeline collapses tool results
/// first (gentle), summarizes older turns (moderate), bounds by sliding window
/// (aggressive), then truncates as an emergency backstop.
/// </para>
///
/// <para>
/// Returning <see langword="null"/> when compaction is disabled lets
/// <c>DocumentChatAppService</c> skip wiring <c>AIContextProviders</c> entirely, keeping
/// the no-compaction path identical to the pre-compaction wiring (zero allocation, zero
/// MAF pipeline overhead).
/// </para>
/// </summary>
public class ChatCompactionStrategyFactory : ITransientDependency
{
    private readonly PaperbaseAIBehaviorOptions _aiOptions;
    private readonly IChatClient _summarizer;

    public ChatCompactionStrategyFactory(
        IOptions<PaperbaseAIBehaviorOptions> aiOptions,
        [FromKeyedServices(PaperbaseAIConsts.SummarizerChatClientKey)] IChatClient summarizer)
    {
        _aiOptions = aiOptions.Value;
        _summarizer = summarizer;
    }

    /// <summary>
    /// Returns a configured <see cref="CompactionProvider"/>, or <see langword="null"/>
    /// when <see cref="ChatCompactionOptions.Enabled"/> is false.
    /// </summary>
    public virtual CompactionProvider? CreateProvider()
    {
        var opts = _aiOptions.ChatCompaction;
        if (!opts.Enabled)
        {
            return null;
        }

        var pipeline = new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(
                CompactionTriggers.TokensExceed(opts.CollapseToolResultsAtTokens)),
            new SummarizationCompactionStrategy(
                _summarizer,
                CompactionTriggers.TokensExceed(opts.SummarizeAtTokens),
                minimumPreservedGroups: opts.MinimumPreservedGroups),
            new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(opts.SlidingWindowTurns)),
            new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed(opts.TruncateAtTokens)));

        return new CompactionProvider(pipeline);
    }
}

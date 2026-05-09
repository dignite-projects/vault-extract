using Dignite.Paperbase.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Chat.Compaction;

/// <summary>
/// Unit-level coverage for the factory boundary: enabled/disabled toggle and the fact
/// that the constructor doesn't crash on the null-summarizer test wiring shape (a real
/// host always provides a summarizer keyed registration; the test module too — see
/// <c>DocumentChatAppServiceTestModule</c>). Internal pipeline structure is verified via
/// the multi-turn integration test in <c>DocumentChat_E2E_Tests</c> rather than by
/// inspecting MAF internals.
/// </summary>
public class ChatCompactionStrategyFactory_Tests
{
    [Fact]
    public void CreateProvider_Returns_Null_When_Disabled()
    {
        var opts = new PaperbaseAIBehaviorOptions { ChatCompaction = { Enabled = false } };
        var factory = new ChatCompactionStrategyFactory(
            Options.Create(opts),
            Substitute.For<IChatClient>());

        factory.CreateProvider().ShouldBeNull();
    }

    [Fact]
    public void CreateProvider_Returns_Provider_When_Enabled()
    {
        var opts = new PaperbaseAIBehaviorOptions
        {
            ChatCompaction =
            {
                Enabled = true,
                CollapseToolResultsAtTokens = 256,
                SummarizeAtTokens = 1024,
                SlidingWindowTurns = 6,
                TruncateAtTokens = 16_384,
                MinimumPreservedGroups = 3,
            }
        };
        var factory = new ChatCompactionStrategyFactory(
            Options.Create(opts),
            Substitute.For<IChatClient>());

        factory.CreateProvider().ShouldNotBeNull();
    }
}

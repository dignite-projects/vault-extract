using System.Collections.Generic;
using Dignite.Paperbase.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Ai;

/// <summary>
/// Test module that stacks an in-memory <c>PaperbaseAIBehavior</c> configuration
/// section on top of whatever <see cref="PaperbaseApplicationTestModule"/> already
/// provides. <see cref="PaperbaseApplicationModule.ConfigureServices"/> binds
/// <see cref="PaperbaseAIBehaviorOptions"/> to that section, so this module is the
/// vehicle that lets the test prove the binding is wired end-to-end.
/// </summary>
[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class PaperbaseAIBehaviorOptionsBindingTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var existing = context.Services.GetConfiguration();
        var stacked = new ConfigurationBuilder()
            .AddConfiguration(existing)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PaperbaseAIBehavior:DefaultLanguage"] = "en",
                ["PaperbaseAIBehavior:EnableLlmRerank"] = "true",
                ["PaperbaseAIBehavior:RecallExpandFactor"] = "7",
                ["PaperbaseAIBehavior:DocumentChatMinScore"] = "0.44",
                ["PaperbaseAIBehavior:ChatCompaction:Enabled"] = "true",
                ["PaperbaseAIBehavior:ChatCompaction:SummarizeAtTokens"] = "2048",
                ["PaperbaseAIBehavior:ChatCompaction:SlidingWindowTurns"] = "12",
            })
            .Build();

        context.Services.ReplaceConfiguration(stacked);
    }
}

/// <summary>
/// Acceptance test for Issue #83: configuration values placed under the
/// <c>PaperbaseAIBehavior</c> JSON section must reach <see cref="PaperbaseAIBehaviorOptions"/>
/// consumers via <see cref="IOptions{T}"/>. Before #83 the Options class had no binding,
/// so every consumer received compile-time defaults regardless of <c>appsettings.json</c>.
/// </summary>
public class PaperbaseAIBehaviorOptionsBinding_Tests
    : PaperbaseApplicationTestBase<PaperbaseAIBehaviorOptionsBindingTestModule>
{
    private readonly PaperbaseAIBehaviorOptions _options;

    public PaperbaseAIBehaviorOptionsBinding_Tests()
    {
        _options = GetRequiredService<IOptions<PaperbaseAIBehaviorOptions>>().Value;
    }

    [Fact]
    public void Configuration_Values_Flow_Through_To_Options()
    {
        // Each assertion would fail with the class default if the
        // PaperbaseAIBehavior → PaperbaseAIBehaviorOptions binding ever regresses.
        _options.DefaultLanguage.ShouldBe("en");                             // default "ja"
        _options.EnableLlmRerank.ShouldBeTrue();                              // default false
        _options.RecallExpandFactor.ShouldBe(7);                              // default 4
        _options.DocumentChatMinScore.ShouldBe(0.44);                         // default 0.45
        _options.ChatCompaction.Enabled.ShouldBeTrue();                       // default false
        _options.ChatCompaction.SummarizeAtTokens.ShouldBe(2048);             // default 1280
        _options.ChatCompaction.SlidingWindowTurns.ShouldBe(12);              // default 8
    }

    [Fact]
    public void Unset_Keys_Fall_Back_To_Class_Defaults()
    {
        // The in-memory config above does not set these keys, so the class
        // defaults must remain in effect. This protects against an accidental
        // wholesale-replace binding that would zero out unspecified fields.
        _options.MaxDocumentTypesInClassificationPrompt.ShouldBe(50);
        _options.MaxTextLengthPerExtraction.ShouldBe(8000);
        _options.ChunkSize.ShouldBe(800);
        _options.ChunkOverlap.ShouldBe(100);
        _options.ChunkBoundaryTolerance.ShouldBe(120);
        _options.MaxTitleGenerationMarkdownLength.ShouldBe(4000);
        // Compaction sub-options not set explicitly — class defaults must persist:
        _options.ChatCompaction.CollapseToolResultsAtTokens.ShouldBe(0x200);
        _options.ChatCompaction.TruncateAtTokens.ShouldBe(0x8000);
        _options.ChatCompaction.MinimumPreservedGroups.ShouldBe(4);
    }
}

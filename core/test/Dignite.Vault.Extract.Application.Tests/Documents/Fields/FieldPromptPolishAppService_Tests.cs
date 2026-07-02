using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Sanitize / fail-open semantics for FieldPromptPolishAppService (#447). IChatClient is substituted (no
/// real LLM). Permission is allowed by the test subclass (the unit is built directly with no HTTP auth
/// context); production is guarded by class-level [Authorize] + the Create||Update check.
/// </summary>
public class FieldPromptPolishAppService_Tests
{
    [DisableConventionalRegistration]
    private sealed class TestableFieldPromptPolishAppService : FieldPromptPolishAppService
    {
        public TestableFieldPromptPolishAppService(IChatClient chatClient, ILogger<FieldPromptPolishAppService> logger)
            : base(chatClient, logger)
        {
        }

        protected override Task CheckPolishPermissionAsync() => Task.CompletedTask;
    }

    private static FieldPromptPolishAppService CreateService(IChatClient chatClient)
        => new TestableFieldPromptPolishAppService(chatClient, NullLogger<FieldPromptPolishAppService>.Instance);

    private static IChatClient ChatClientReturning(string responseText)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)])));
        return fake;
    }

    private static IChatClient ChatClientThrowing(Exception ex)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(ex));
        return fake;
    }

    [Fact]
    public async Task Returns_polished_markdown_trimmed()
    {
        var svc = CreateService(ChatClientReturning("\n## Amount\n\nExtract the invoice total.\n"));

        var result = await svc.PolishAsync(new FieldPromptPolishInput { Prompt = "get the amount" });

        result.Prompt.ShouldBe("## Amount\n\nExtract the invoice total.");
    }

    [Fact]
    public async Task Strips_a_wrapping_markdown_code_fence()
    {
        // Weakly-instructed providers sometimes fence the whole reply despite the "no code fences" rule.
        var svc = CreateService(ChatClientReturning("```markdown\n## Amount\n\nExtract the total.\n```"));

        var result = await svc.PolishAsync(new FieldPromptPolishInput { Prompt = "amount" });

        result.Prompt.ShouldBe("## Amount\n\nExtract the total.");
        result.Prompt.ShouldNotContain("```");
    }

    [Fact]
    public async Task Falls_back_to_original_when_llm_throws()
    {
        var svc = CreateService(ChatClientThrowing(new InvalidOperationException("LLM down")));

        var result = await svc.PolishAsync(new FieldPromptPolishInput { Prompt = "keep me" });

        // Fail-open: the button must never destroy the operator's input.
        result.Prompt.ShouldBe("keep me");
    }

    [Fact]
    public async Task Falls_back_to_original_when_output_is_blank()
    {
        var svc = CreateService(ChatClientReturning("   \n  "));

        var result = await svc.PolishAsync(new FieldPromptPolishInput { Prompt = "keep me too" });

        result.Prompt.ShouldBe("keep me too");
    }
}

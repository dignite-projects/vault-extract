using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Fallback / cancellation semantics for SlugSuggestionAppService (issue #190 + Codex adversarial review
/// finding 2). IChatClient is replaced with NSubstitute, with no real LLM calls. These tests do not verify
/// CancelAfter timing itself, which is standard .NET behavior; they verify only the service-owned logic
/// for routing OperationCanceledException and degrading exceptions / bad output to an empty slug.
/// </summary>
public class SlugSuggestionAppService_Tests
{
    private static SlugSuggestionAppService CreateService(IChatClient chatClient)
        => new(chatClient, NullLogger<SlugSuggestionAppService>.Instance);

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
    public async Task Returns_sanitized_snake_case_slug_from_json()
    {
        var svc = CreateService(ChatClientReturning("{\"slug\": \"Contract Amount!\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        // Server-side sanitize: lowercase, fold non-[a-z0-9] to underscores, merge, and trim edges.
        result.Slug.ShouldBe("contract_amount");
    }

    [Fact]
    public async Task Returns_empty_when_output_is_not_json()
    {
        var svc = CreateService(ChatClientReturning("sorry, I cannot help with that"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_slug_key_missing()
    {
        var svc = CreateService(ChatClientReturning("{\"name\": \"contract_amount\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_slug_has_no_ascii_after_sanitize()
    {
        // LLM did not translate and returned CJK as-is; after sanitize there are no legal characters, so
        // slug is empty and the frontend falls back to a local placeholder.
        var svc = CreateService(ChatClientReturning("{\"slug\": \"合同\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_llm_throws()
    {
        var svc = CreateService(ChatClientThrowing(new InvalidOperationException("provider down")));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_on_server_deadline_cancellation()
    {
        // Simulate server deadline (CancelAfter): caller token is not canceled, but the LLM call throws
        // OperationCanceledException. This should degrade to empty slug rather than throw upward.
        var svc = CreateService(ChatClientThrowing(new OperationCanceledException()));

        var result = await svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Propagates_cancellation_when_caller_cancels()
    {
        // Client disconnect: caller token is canceled and the LLM throws OperationCanceledException. Throw
        // it upward as-is instead of swallowing it as an LLM failure.
        var svc = CreateService(ChatClientThrowing(new OperationCanceledException()));
        var canceled = new CancellationToken(canceled: true);

        await Should.ThrowAsync<OperationCanceledException>(
            () => svc.SuggestAsync(new SuggestSlugInput { Label = "合同金额" }, canceled));
    }
}

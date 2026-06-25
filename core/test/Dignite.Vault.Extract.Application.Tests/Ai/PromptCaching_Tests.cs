using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Verifies UseDistributedCache middleware caching behavior: when the same input hits cache on the second
/// call, the underlying IChatClient is not called.
/// </summary>
public class PromptCaching_Tests
{
    [Fact]
    public async Task UseDistributedCache_ServesSecondIdenticalCallFromCache()
    {
        var callCount = 0;
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
            });

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddLogging();
        services.AddChatClient(_ => inner).UseDistributedCache();

        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClient>();

        var messages = new List<ChatMessage> { new(ChatRole.User, "classify this document text") };

        await client.GetResponseAsync(messages);   // cache miss → calls inner
        await client.GetResponseAsync(messages);   // cache hit  → skips inner

        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task UseDistributedCache_DifferentInputsBothCallInner()
    {
        var callCount = 0;
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
            });

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddLogging();
        services.AddChatClient(_ => inner).UseDistributedCache();

        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClient>();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "document A text")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "document B text")]);

        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task WithoutDistributedCache_EachCallGoesToInner()
    {
        var callCount = 0;
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChatClient(_ => inner);   // no UseDistributedCache

        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClient>();

        var messages = new List<ChatMessage> { new(ChatRole.User, "classify this document text") };

        await client.GetResponseAsync(messages);
        await client.GetResponseAsync(messages);

        callCount.ShouldBe(2);
    }
}

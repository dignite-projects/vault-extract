using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using MeAi = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

public class DocumentChatHistoryProvider_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IChatConversationRepository _repository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Volo.Abp.Timing.IClock _clock;

    public DocumentChatHistoryProvider_Tests()
    {
        _repository = GetRequiredService<IChatConversationRepository>();
        _scopeFactory = GetRequiredService<IServiceScopeFactory>();
        _clock = GetRequiredService<Volo.Abp.Timing.IClock>();
    }

    [Fact]
    public async Task Should_Load_History_From_Conversation_Repository()
    {
        var conversationId = await CreateConversationWithMessagesAsync();
        var provider = new DocumentChatHistoryProvider(_scopeFactory);

        var messages = (await provider.LoadHistoryAsync(conversationId)).ToList();

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe(ChatRole.User);
        messages[0].Text.ShouldBe("hello");
        messages[1].Role.ShouldBe(ChatRole.Assistant);
        messages[1].Text.ShouldBe("hi");
    }

    [Fact]
    public async Task Should_Return_Empty_When_Conversation_Missing()
    {
        // Conversation id never inserted — provider treats this as "no prior history"
        // rather than throwing, so the chat AppService can start a fresh turn cleanly
        // even if the conversation has been deleted between authorization and load.
        var provider = new DocumentChatHistoryProvider(_scopeFactory);

        var messages = await provider.LoadHistoryAsync(Guid.NewGuid());

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_Use_Fresh_DI_Scope_Per_Call()
    {
        // The provider is invoked from possibly background / non-HTTP contexts where the
        // ambient scope may be missing or stale; each call must create its own scope so
        // the IChatConversationRepository resolution is always valid.
        var conversationId = Guid.NewGuid();
        var conversation = new ChatConversation(
            conversationId,
            tenantId: null,
            title: "Test",
            documentId: null);

        var repository = Substitute.For<IChatConversationRepository>();
        repository.FindByIdWithMessagesAsync(
                conversationId,
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(conversation);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IChatConversationRepository)).Returns(repository);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var provider = new DocumentChatHistoryProvider(scopeFactory);

        await provider.LoadHistoryAsync(conversationId);
        await provider.LoadHistoryAsync(conversationId);

        scopeFactory.Received(2).CreateScope();
    }

    [Fact]
    public async Task Should_Return_Messages_In_Chronological_Order()
    {
        // MAF prepends history into the LLM request in the order returned, so ordering
        // must be ascending by creation time. Role alternation (user → assistant) of the
        // seeded data is the proxy for creation-time ordering since CreationTime is not
        // observable on MeAi.ChatMessage.
        var conversationId = await CreateConversationWithMessagesAsync();
        var provider = new DocumentChatHistoryProvider(_scopeFactory);

        var messages = (await provider.LoadHistoryAsync(conversationId)).ToList();

        for (var i = 1; i < messages.Count; i++)
        {
            messages[i - 1].Role.ShouldBe(ChatRole.User);
            messages[i].Role.ShouldBe(ChatRole.Assistant);
        }
    }

    [Fact]
    public async Task Provider_Returns_Empty_When_StateBag_Missing_ConversationId()
    {
        // ProvideChatHistoryAsync is the entry point MAF actually uses (via
        // InvokingAsync inside the agent pipeline). With no conversation id stashed in
        // the session StateBag, the provider must yield an empty history rather than
        // failing — that's how a freshly-created session behaves before the AppService
        // wires it up.
        var provider = new DocumentChatHistoryProvider(_scopeFactory);
        var session = new TestAgentSession();

        var messages = (await InvokeProviderAsync(provider, session)).ToList();

        // Default InvokingCoreAsync concatenates [] history with the request messages
        // we passed in, so we expect just the request message back unchanged.
        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe(ChatRole.User);
        messages[0].Text.ShouldBe("ping");
    }

    [Fact]
    public async Task Provider_Loads_History_From_StateBag_ConversationId()
    {
        // End-to-end through the MAF entry point: write the conversation id to the
        // session StateBag (the AppService side of the contract), then verify the
        // provider's InvokingAsync returns history-then-request, with the history
        // messages stamped as ChatHistory source so downstream context providers
        // (CompactionProvider) can distinguish them.
        var conversationId = await CreateConversationWithMessagesAsync();
        var provider = new DocumentChatHistoryProvider(_scopeFactory);
        var session = new TestAgentSession();

        session.StateBag.SetValue(
            DocumentChatHistoryProvider.SessionStateKey,
            new DocumentChatSessionState(conversationId));

        var messages = (await InvokeProviderAsync(provider, session)).ToList();

        // History (2) + new request (1)
        messages.Count.ShouldBe(3);
        messages[0].Text.ShouldBe("hello");
        messages[1].Text.ShouldBe("hi");
        messages[2].Text.ShouldBe("ping");

        // History entries must be source-stamped so CompactionProvider treats them as
        // chat history rather than fresh request when it integrates next.
        messages[0].GetAgentRequestMessageSourceType().ShouldBe(AgentRequestMessageSourceType.ChatHistory);
        messages[1].GetAgentRequestMessageSourceType().ShouldBe(AgentRequestMessageSourceType.ChatHistory);
        messages[2].GetAgentRequestMessageSourceType().ShouldNotBe(AgentRequestMessageSourceType.ChatHistory);
    }

    private static async Task<IEnumerable<MeAi.ChatMessage>> InvokeProviderAsync(
        DocumentChatHistoryProvider provider,
        AgentSession session)
    {
        // InvokingContext ctor is annotated [Experimental(MAAI001)]; suppression is
        // scoped to this helper so production code stays clean of the diagnostic.
        // NSubstitute can stand in for AIAgent (an abstract class) since we only need
        // a non-null reference for the ctor's null-guard — InvokingCoreAsync never
        // touches its members.
#pragma warning disable MAAI001
        var agent = Substitute.For<AIAgent>();
        var ctx = new ChatHistoryProvider.InvokingContext(
            agent,
            session,
            [new MeAi.ChatMessage(ChatRole.User, "ping")]);
#pragma warning restore MAAI001
        return await provider.InvokingAsync(ctx);
    }

    private async Task<Guid> CreateConversationWithMessagesAsync()
    {
        return await WithUnitOfWorkAsync(async () =>
        {
            var conversation = new ChatConversation(
                Guid.NewGuid(),
                tenantId: null,
                title: "Test",
                documentId: null);

            conversation.AppendUserMessage(_clock, Guid.NewGuid(), "hello", Guid.NewGuid());
            conversation.AppendAssistantMessage(
                _clock,
                Guid.NewGuid(),
                "hi",
                citationsJson: null,
                isDegraded: false);
            await _repository.InsertAsync(conversation, autoSave: true);

            return conversation.Id;
        });
    }

    /// <summary>
    /// Minimal in-test <see cref="AgentSession"/>: real ChatClient sessions are created
    /// by <c>ChatClientAgent.CreateSessionAsync</c> which requires a live LLM client;
    /// this stand-in avoids that dependency since the provider only touches StateBag.
    /// </summary>
    private sealed class TestAgentSession : AgentSession
    {
        public TestAgentSession() : base(new AgentSessionStateBag()) { }
    }
}

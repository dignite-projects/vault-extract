using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp.Security.Claims;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Behavioral tests for the "model declines to invoke the search tool" branch of
/// the single MAF tool-calling chat path. When the substituted <see cref="IChatClient"/>
/// returns a plain text answer (no <c>FunctionCallContent</c>), the search AIFunction is
/// never invoked, the knowledge index is never queried, and
/// <see cref="ChatTurnResultDto.IsDegraded"/> must be <c>true</c> — the honest signal
/// that the answer was not grounded in retrieved sources.
/// </summary>
public class DocumentChatDegradedSignal_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IChatClient _chatClient;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public DocumentChatDegradedSignal_Tests()
    {
        _appService         = GetRequiredService<IDocumentChatAppService>();
        _chatClient         = GetRequiredService<IChatClient>();
        _knowledgeIndex     = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor  = GetRequiredService<ICurrentPrincipalAccessor>();

        SetupDefaultEmbedding();
        SetupDefaultKnowledgeIndex();
        SetupDefaultSyncChatClient();
        SetupDefaultStreamingChatClient();
    }

    [Fact]
    public async Task SendMessageAsync_IsDegraded_True_When_Model_Does_Not_Call_Search_Tool()
    {
        var conversationId = await CreateConversationAsync();

        ChatTurnResultDto result = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                result = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message      = "What are the payment terms?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        // The substituted IChatClient never returns FunctionCallContent, so no tool
        // (search or otherwise) is invoked. Issue #99 redefined IsDegraded as
        // "GroundingSource == None"; both signals must agree.
        result.IsDegraded.ShouldBeTrue();
        result.GroundingSource.ShouldBe(GroundingSource.None);
    }

    [Fact]
    public async Task SendMessageAsync_KnowledgeIndex_Not_Called_When_Model_Does_Not_Call_Search_Tool()
    {
        var conversationId = await CreateConversationAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message      = "What are the payment terms?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        // The search AIFunction is the only path to IDocumentKnowledgeIndex.SearchAsync;
        // when the model never invokes it, the knowledge index must never be queried.
        await _knowledgeIndex.DidNotReceive().SearchAsync(
            Arg.Any<VectorSearchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_Replay_Preserves_Degraded_Signal()
    {
        var conversationId = await CreateConversationAsync();
        var clientTurnId = Guid.NewGuid();

        ChatTurnResultDto first = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                first = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "What are the payment terms?",
                    ClientTurnId = clientTurnId
                });
            }
        });

        ChatTurnResultDto replay = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                replay = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "What are the payment terms?",
                    ClientTurnId = clientTurnId
                });
            }
        });

        first.IsDegraded.ShouldBeTrue();
        replay.IsDegraded.ShouldBeTrue();
        replay.UserMessageId.ShouldBe(first.UserMessageId);
        replay.AssistantMessageId.ShouldBe(first.AssistantMessageId);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageStreamingAsync_IsDegraded_True_When_Model_Does_Not_Call_Search_Tool()
    {
        var conversationId = await CreateConversationAsync();

        ChatTurnDeltaDto? done = null;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() }))
                {
                    if (delta.Kind == ChatTurnDeltaKind.Done)
                        done = delta;
                }
            }
        });

        done.ShouldNotBeNull();
        done!.IsDegraded.ShouldBeTrue();
        done.GroundingSource.ShouldBe(GroundingSource.None);
    }

    [Fact]
    public async Task SendMessageStreamingAsync_KnowledgeIndex_Not_Called_When_Model_Does_Not_Call_Search_Tool()
    {
        var conversationId = await CreateConversationAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var _ in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() }))
                { }
            }
        });

        await _knowledgeIndex.DidNotReceive().SearchAsync(
            Arg.Any<VectorSearchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageStreamingAsync_Replay_Preserves_Degraded_Signal()
    {
        var conversationId = await CreateConversationAsync();
        var clientTurnId = Guid.NewGuid();

        ChatTurnDeltaDto? firstDone = null;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = clientTurnId }))
                {
                    if (delta.Kind == ChatTurnDeltaKind.Done)
                        firstDone = delta;
                }
            }
        });

        ChatTurnDeltaDto? replayDone = null;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = clientTurnId }))
                {
                    if (delta.Kind == ChatTurnDeltaKind.Done)
                        replayDone = delta;
                }
            }
        });

        firstDone.ShouldNotBeNull();
        replayDone.ShouldNotBeNull();
        firstDone!.IsDegraded.ShouldBeTrue();
        replayDone!.IsDegraded.ShouldBeTrue();
        replayDone.UserMessageId.ShouldBe(firstDone.UserMessageId);
        replayDone.AssistantMessageId.ShouldBe(firstDone.AssistantMessageId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateConversationAsync()
    {
        return await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Degraded-Signal Test"
                });
                return dto.Id;
            }
        });
    }

    private IDisposable ChangeUser(Guid userId)
    {
        var claims = new List<Claim> { new(AbpClaimTypes.UserId, userId.ToString()) };
        return _principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    private void SetupDefaultEmbedding()
    {
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });
        _embeddingGenerator
            .GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
    }

    private void SetupDefaultKnowledgeIndex()
    {
        _knowledgeIndex
            .SearchAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());
    }

    private void SetupDefaultSyncChatClient()
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, "stub answer")])));
    }

    private void SetupDefaultStreamingChatClient()
    {
        _chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => FakeStream(new[] { "stub ", "answer" }));
    }

    private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> FakeStream(
        IEnumerable<string> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
        }
    }
}

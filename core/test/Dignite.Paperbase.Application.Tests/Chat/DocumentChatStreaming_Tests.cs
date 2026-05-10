using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Security.Claims;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Tests for <see cref="IDocumentChatAppService.SendMessageStreamingAsync"/>. Exercises:
/// SSE delta sequence, fail-closed authorization gate (streaming path), per-turn
/// idempotency, single-shot persistence, error event on LLM exception, and
/// cancellation (no persistence when stream is cancelled).
/// </summary>
public class DocumentChatStreaming_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IChatConversationRepository _repository;
    private readonly IChatClient _chatClient;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public DocumentChatStreaming_Tests()
    {
        _appService         = GetRequiredService<IDocumentChatAppService>();
        _repository         = GetRequiredService<IChatConversationRepository>();
        _chatClient         = GetRequiredService<IChatClient>();
        _knowledgeIndex     = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor  = GetRequiredService<ICurrentPrincipalAccessor>();

        SetupDefaultEmbedding();
        SetupDefaultKnowledgeIndex();
        SetupStreamingChatClient(new[] { "Hello", " ", "World" });
    }

    // ── 1. Delta sequence: N PartialText events followed by exactly one Done ──

    [Fact]
    public async Task Should_Yield_PartialText_Deltas_Then_Done()
    {
        var conversationId = await CreateConversationAsync();
        var deltas = new List<ChatTurnDeltaDto>();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() }))
                {
                    deltas.Add(delta);
                }
            }
        });

        deltas.Count.ShouldBe(4); // 3 PartialText + 1 Done
        deltas[0].Kind.ShouldBe(ChatTurnDeltaKind.PartialText);
        deltas[0].Text.ShouldBe("Hello");
        deltas[1].Kind.ShouldBe(ChatTurnDeltaKind.PartialText);
        deltas[1].Text.ShouldBe(" ");
        deltas[2].Kind.ShouldBe(ChatTurnDeltaKind.PartialText);
        deltas[2].Text.ShouldBe("World");
        deltas[3].Kind.ShouldBe(ChatTurnDeltaKind.Done);
        deltas[3].UserMessageId.ShouldNotBe(Guid.Empty);
        deltas[3].AssistantMessageId.ShouldNotBe(Guid.Empty);
    }

    // ── 2. Persist exactly once (not per chunk) ───────────────────────────────

    [Fact]
    public async Task Should_Persist_Exactly_Two_Messages_After_Stream_Completes()
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

        var conv = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
                return await _repository.FindByIdWithMessagesAsync(conversationId, 50);
        });

        conv!.Messages.Count.ShouldBe(2);
        conv.Messages.Count(m => m.Role == ChatMessageRole.User).ShouldBe(1);
        conv.Messages.Count(m => m.Role == ChatMessageRole.Assistant).ShouldBe(1);
    }

    // ── 3. Persisted assistant text == concatenation of all PartialText deltas ─

    [Fact]
    public async Task Persisted_Assistant_Text_Matches_Concatenated_Deltas()
    {
        var conversationId = await CreateConversationAsync();
        var partials = new List<string>();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() }))
                {
                    if (delta.Kind == ChatTurnDeltaKind.PartialText)
                        partials.Add(delta.Text!);
                }
            }
        });

        var assembled = string.Concat(partials);
        assembled.ShouldBe("Hello World");

        var conv = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
                return await _repository.FindByIdWithMessagesAsync(conversationId, 50);
        });

        var assistantMsg = conv!.Messages.First(m => m.Role == ChatMessageRole.Assistant);
        assistantMsg.Content.ShouldBe(assembled);
    }

    // ── 4. Idempotency: same ClientTurnId → single Done event, model called once ─

    [Fact]
    public async Task Should_Be_Idempotent_For_Same_ClientTurnId_In_Streaming()
    {
        var conversationId = await CreateConversationAsync();
        var clientTurnId = Guid.NewGuid();

        var firstDeltas  = await CollectDeltasAsync(conversationId, "q", clientTurnId);
        var secondDeltas = await CollectDeltasAsync(conversationId, "q", clientTurnId);

        // Replay must return exactly one Done event with the same persisted IDs.
        secondDeltas.Count.ShouldBe(1);
        secondDeltas[0].Kind.ShouldBe(ChatTurnDeltaKind.Done);
        secondDeltas[0].UserMessageId.ShouldBe(firstDeltas.Last().UserMessageId);
        secondDeltas[0].AssistantMessageId.ShouldBe(firstDeltas.Last().AssistantMessageId);

        // LLM streaming was invoked exactly once across both calls.
        _chatClient.Received(1).GetStreamingResponseAsync(
            Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    // ── 5. Error event on LLM exception ──────────────────────────────────────

    [Fact]
    public async Task Should_Emit_Error_Event_When_LLM_Throws()
    {
        _chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ThrowAfterOneChunk());

        var conversationId = await CreateConversationAsync();
        var deltas = new List<ChatTurnDeltaDto>();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() }))
                {
                    deltas.Add(delta);
                }
            }
        });

        // Last event must be Error; internal exception detail must not be exposed.
        deltas.ShouldNotBeEmpty();
        var last = deltas.Last();
        last.Kind.ShouldBe(ChatTurnDeltaKind.Error);
        last.ErrorMessage.ShouldNotBeNullOrEmpty();
        last.ErrorMessage!.ShouldNotContain("LLM exploded");
    }

    // ── 6. Fail-closed gate: non-owner gets EntityNotFoundException ───────────

    [Fact]
    public async Task Should_Throw_EntityNotFoundException_For_Non_Owner_Streaming()
    {
        var conversationId = await CreateConversationAsync();

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OtherUserId))
                {
                    await foreach (var _ in _appService.SendMessageStreamingAsync(
                        conversationId,
                        new SendChatMessageInput { Message = "leak", ClientTurnId = Guid.NewGuid() }))
                    { }
                }
            });
        });
    }

    // ── 7. Cancellation: no persistence when token is pre-cancelled ───────────

    [Fact]
    public async Task Should_Not_Persist_On_Cancellation()
    {
        var conversationId = await CreateConversationAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await foreach (var _ in _appService.SendMessageStreamingAsync(
                        conversationId,
                        new SendChatMessageInput { Message = "q", ClientTurnId = Guid.NewGuid() },
                        cts.Token))
                    { }
                }
            });
        }
        catch (OperationCanceledException) { /* expected when token is pre-cancelled */ }

        var conv = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
                return await _repository.FindByIdWithMessagesAsync(conversationId, 50);
        });

        conv!.Messages.ShouldBeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<ChatTurnDeltaDto>> CollectDeltasAsync(
        Guid conversationId, string message, Guid clientTurnId)
    {
        var deltas = new List<ChatTurnDeltaDto>();
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await foreach (var delta in _appService.SendMessageStreamingAsync(
                    conversationId,
                    new SendChatMessageInput { Message = message, ClientTurnId = clientTurnId }))
                {
                    deltas.Add(delta);
                }
            }
        });
        return deltas;
    }

    private async Task<Guid> CreateConversationAsync()
    {
        return await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Streaming Test"
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

    private void SetupStreamingChatClient(string[] chunks)
    {
        _chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => FakeStream(chunks));
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

    private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> ThrowAfterOneChunk(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "partial");
        throw new InvalidOperationException("LLM exploded");
    }
}

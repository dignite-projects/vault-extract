using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Security.Claims;
using Volo.Abp.Validation;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Behavioral tests for <see cref="DocumentChatAppService"/>. Exercises:
/// fail-closed authorization (tenant + ownership), per-turn idempotency,
/// optimistic-concurrency surfacing, and multi-turn history propagation. Search
/// scope / citation / chunk-formatting concerns live in
/// <see cref="Search.DocumentTextSearchAdapter_Tests"/> at the adapter level.
/// </summary>
public class DocumentChatAppService_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IChatConversationRepository _repository;
    private readonly IChatClient _chatClient;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly ICurrentTenant _currentTenant;
    private readonly Volo.Abp.Timing.IClock _clock;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public DocumentChatAppService_Tests()
    {
        _appService = GetRequiredService<IDocumentChatAppService>();
        _repository = GetRequiredService<IChatConversationRepository>();
        _chatClient = GetRequiredService<IChatClient>();
        _knowledgeIndex = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
        _clock = GetRequiredService<Volo.Abp.Timing.IClock>();

        SetupDefaultEmbedding();
        SetupDefaultChatClient();
        SetupDefaultKnowledgeIndex();
    }

    // ── 1. CreateConversation: happy path ────────────────────────────────────

    [Fact]
    public async Task Should_Create_Conversation_When_Input_Is_Valid()
    {
        // Issue #100: only Title + optional DocumentId remain on the input. Per-turn
        // scope (TopK / MinScore / DocumentTypeCode) is decided by the model, not pinned.
        var dto = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "My contract"
                });
            }
        });

        dto.ShouldNotBeNull();
        dto.Title.ShouldBe("My contract");
        dto.DocumentId.ShouldBeNull();
    }

    // Issue #100 retired the DocumentId-vs-DocumentTypeCode mutual-exclusion test —
    // there is no DocumentTypeCode on CreateChatConversationInput anymore, so the
    // conflict cannot be expressed.

    // ── 3. SendMessage: history is carried across turns ─────────────────────

    [Fact]
    public async Task Should_Send_Multi_Turn_Messages_And_Carry_History()
    {
        var conversationId = await CreateConversationAsync();

        // Capture every IChatClient call so we can inspect the history payload of each turn.
        var capturedTurns = new List<List<MEAI.ChatMessage>>();
        _chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<MEAI.ChatMessage>>(msgs =>
                    capturedTurns.Add(msgs.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var n = capturedTurns.Count; // 1-indexed turn marker
                return Task.FromResult(
                    new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, $"answer-{n}")]));
            });

        for (var i = 1; i <= 3; i++)
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message = $"q-{i}",
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        }

        capturedTurns.Count.ShouldBe(3);
        capturedTurns[0].Count.ShouldBe(1);
        capturedTurns[1].Count.ShouldBe(3);
        capturedTurns[2].Count.ShouldBe(5);
        capturedTurns[0].Select(m => m.Text).ShouldContain("q-1");
        capturedTurns[1].Select(m => m.Text).ShouldContain("q-1");
        capturedTurns[1].Select(m => m.Text).ShouldContain("answer-1");
        capturedTurns[1].Select(m => m.Text).ShouldContain("q-2");
        capturedTurns[2].Select(m => m.Text).ShouldContain("q-1");
        capturedTurns[2].Select(m => m.Text).ShouldContain("answer-1");
        capturedTurns[2].Select(m => m.Text).ShouldContain("q-2");
        capturedTurns[2].Select(m => m.Text).ShouldContain("answer-2");
        capturedTurns[2].Select(m => m.Text).ShouldContain("q-3");

        var conv = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _repository.FindByIdWithMessagesAsync(conversationId, 50);
            }
        });
        conv!.Messages.Count.ShouldBe(6);
    }

    // ── 4. Cross-tenant: 404 ────────────────────────────────────────────────

    [Fact]
    public async Task Should_Return_404_For_Cross_Tenant_Access()
    {
        var conversationId = await CreateConversationAsync();

        var otherTenantId = Guid.NewGuid();
        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                // Same user, different tenant — fail-closed gate must reject.
                using (ChangeUser(OwnerUserId))
                using (_currentTenant.Change(otherTenantId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message = "leak attempt",
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        });
    }

    // ── 6. Non-owner: 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Should_Return_404_For_Non_Owner()
    {
        var conversationId = await CreateConversationAsync();

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OtherUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message = "leak attempt",
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        });
    }

    // ── 7. Idempotency: same ClientTurnId returns same result, model called once ──

    [Fact]
    public async Task Should_Be_Idempotent_For_Same_ClientTurnId()
    {
        var conversationId = await CreateConversationAsync();
        var clientTurnId = Guid.NewGuid();

        var first = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "q",
                    ClientTurnId = clientTurnId
                });
            }
        });

        var second = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "q",
                    ClientTurnId = clientTurnId
                });
            }
        });

        // Replay returns the persisted ids — never mints new ones.
        second.UserMessageId.ShouldBe(first.UserMessageId);
        second.AssistantMessageId.ShouldBe(first.AssistantMessageId);

        // Model must have been invoked exactly once across both posts.
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Generate_Title_After_First_Turn_When_Title_Is_Default()
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Is<ChatOptions?>(o => o != null && o.Instructions != null && o.Instructions.Contains("conversation titles")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, "\"Contract Renewal\"")])));

        var conversationId = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput());
                return dto.Id;
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "When does this contract renew?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        var conversation = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _repository.GetAsync(conversationId);
            }
        });

        conversation.Title.ShouldBe("Contract Renewal");
    }

    // ── 8. Concurrency conflict surfaces as AbpDbConcurrencyException ───────

    [Fact]
    public async Task Should_Reject_Concurrent_Sends_With_409()
    {
        var conversationId = await CreateConversationAsync();

        // Read the aggregate on a separate UoW so we can hold a stale copy after a
        // competing turn rotates the row's ConcurrencyStamp.
        ChatConversation staleAggregate = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                staleAggregate = (await _repository.FindByIdWithMessagesAsync(conversationId, 50))!;
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "first",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        // Updating the stale aggregate must surface AbpDbConcurrencyException, which
        // ABP HTTP layer maps to 409 Conflict; the AppService must NOT catch it.
        await Should.ThrowAsync<AbpDbConcurrencyException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    staleAggregate.AppendUserMessage(_clock, Guid.NewGuid(), "stale racer", Guid.NewGuid());
                    await _repository.UpdateAsync(staleAggregate, autoSave: true);
                }
            });
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateConversationAsync()
        => await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Test"
                });
                return dto.Id;
            }
        });

    private IDisposable ChangeUser(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(AbpClaimTypes.UserId, userId.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return _principalAccessor.Change(principal);
    }

    private void SetupDefaultEmbedding()
    {
        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        var embedding = new Embedding<float>(vector);
        var embeddings = new GeneratedEmbeddings<Embedding<float>>([embedding]);

        _embeddingGenerator
            .GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(embeddings);
    }

    private void SetupDefaultChatClient()
    {
        _chatClient.GetService(Arg.Any<Type>(), Arg.Any<object?>()).Returns(null);

        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, "stub answer")])));
    }

    private void SetupDefaultKnowledgeIndex()
    {
        _knowledgeIndex
            .SearchAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());
    }
}

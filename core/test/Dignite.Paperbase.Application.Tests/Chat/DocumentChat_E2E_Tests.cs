using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.EntityFrameworkCore;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Security.Claims;
using Volo.Abp.Validation;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// End-to-end integration tests that exercise full request paths the unit tests in
/// <see cref="DocumentChatAppService_Tests"/> do not cover: CRUD lifecycle, pagination,
/// multi-turn history depth, search scope variants, and boundary / negation paths.
///
/// Uses the same <see cref="DocumentChatAppServiceTestModule"/> (substituted IChatClient +
/// IDocumentKnowledgeIndex; SQLite in-memory; always-allow authorization).
/// Does NOT call a real LLM or vector store.
/// </summary>
public class DocumentChat_E2E_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IChatConversationRepository _conversationRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IChatClient _chatClient;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;

    private static readonly Guid OwnerUserId  = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId  = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public DocumentChat_E2E_Tests()
    {
        _appService             = GetRequiredService<IDocumentChatAppService>();
        _conversationRepository = GetRequiredService<IChatConversationRepository>();
        _documentRepository     = GetRequiredService<IDocumentRepository>();
        _chatClient             = GetRequiredService<IChatClient>();
        _knowledgeIndex         = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator     = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor      = GetRequiredService<ICurrentPrincipalAccessor>();
        _currentTenant          = GetRequiredService<ICurrentTenant>();
        _dataFilter             = GetRequiredService<IDataFilter>();

        SetupDefaultChatClient();
        SetupDefaultEmbedding();
        SetupDefaultKnowledgeIndex();
    }

    // ── 1. CRUD lifecycle (end-to-end round-trip) ──────────────────────────────

    [Fact]
    public async Task Should_Round_Trip_Conversation_Lifecycle()
    {
        // ─ Create ─
        Guid conversationId = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Lifecycle E2E"
                });
                conversationId = dto.Id;
                dto.Title.ShouldBe("Lifecycle E2E");
            }
        });

        // ─ List — must be visible ─
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var list = await _appService.GetConversationListAsync(new GetChatConversationListInput());
                list.Items.ShouldContain(c => c.Id == conversationId);
            }
        });

        // ─ Get detail ─
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var detail = await _appService.GetConversationAsync(conversationId);
                detail.Id.ShouldBe(conversationId);
                detail.Title.ShouldBe("Lifecycle E2E");
            }
        });

        // ─ Send 2 messages (each turn produces user + assistant rows) ─
        for (var i = 1; i <= 2; i++)
        {
            var turn = i;
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message        = $"question-{turn}",
                        ClientTurnId   = Guid.NewGuid()
                    });
                }
            });
        }

        // ─ Page message list — 2 turns × 2 rows each = 4 total ─
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var messages = await _appService.GetMessageListAsync(conversationId,
                    new GetChatMessageListInput { MaxResultCount = 10, SkipCount = 0 });
                messages.TotalCount.ShouldBe(4);
                messages.Items.Count.ShouldBe(4);
            }
        });

        // ─ Delete ─
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.DeleteConversationAsync(conversationId);
            }
        });

        // ─ List — must no longer appear ─
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var list = await _appService.GetConversationListAsync(new GetChatConversationListInput());
                list.Items.ShouldNotContain(c => c.Id == conversationId);
            }
        });

        // ─ Get — must throw EntityNotFoundException ─
        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.GetConversationAsync(conversationId);
                }
            });
        });
    }

    // ── 2. After soft-delete, messages are inaccessible via AppService; direct EF
    //       query pins the physical DB state (soft-delete does NOT cascade to ChatMessage
    //       because ChatMessage is not ISoftDelete — rows remain until a hard-delete or
    //       a future cleanup job removes them). ─────────────────────────────────────────

    [Fact]
    public async Task Should_Block_Message_Access_After_Conversation_Deleted()
    {
        var conversationId = await CreateConversationAsync();

        // Send one message (user + assistant = 2 DB rows).
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message      = "to be orphaned",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        // Verify messages are accessible before delete.
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var before = await _appService.GetMessageListAsync(conversationId,
                    new GetChatMessageListInput { MaxResultCount = 10 });
                before.TotalCount.ShouldBe(2);
            }
        });

        // Delete conversation (ABP soft-deletes the ChatConversation row).
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.DeleteConversationAsync(conversationId);
            }
        });

        // AppService access is blocked: soft-deleted conversation fails LoadAndAuthorizeAsync.
        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.GetMessageListAsync(conversationId,
                        new GetChatMessageListInput { MaxResultCount = 10 });
                }
            });
        });

        // Test-only: direct DbSet access to ChatMessage (a child entity of ChatConversation)
        // is intentional here to pin physical DB state. Production code MUST always access
        // ChatMessage through ChatConversation.Messages (aggregate root boundary).
        // ChatMessage does not implement ISoftDelete, so its rows remain physically present
        // after the parent is soft-deleted (DB-level CASCADE fires only on hard DELETE).
        // This assertion pins the current behavior — if a future cleanup mechanism removes
        // the rows, this test will fail and draw attention.
        await WithUnitOfWorkAsync(async () =>
        {
            var dbCtxProvider = GetRequiredService<IDbContextProvider<PaperbaseDbContext>>();
            var dbContext = await dbCtxProvider.GetDbContextAsync();
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var physicalCount = dbContext.Set<ChatMessage>()
                    .Count(m => m.ConversationId == conversationId);
                physicalCount.ShouldBe(2);
            }
        });
    }

    // ── 3. Pagination: enough turns to require multiple pages ──────────────────

    [Fact]
    public async Task Should_Page_Message_List_Correctly()
    {
        var conversationId = await CreateConversationAsync();

        // Send 13 turns → 26 messages (user + assistant per turn).
        for (var i = 0; i < 13; i++)
        {
            var idx = i;
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message      = $"q-{idx}",
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        }

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var page = await _appService.GetMessageListAsync(conversationId,
                    new GetChatMessageListInput { MaxResultCount = 10, SkipCount = 10 });
                page.TotalCount.ShouldBe(26);
                page.Items.Count.ShouldBe(10);
            }
        });
    }

    [Fact]
    public async Task Should_Filter_Conversation_List_By_DocumentId()
    {
        var documentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var otherDocumentId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var scopedConversationId = await CreateDocumentScopedConversationAsync(documentId, "Scoped");
        var otherDocumentConversationId = await CreateDocumentScopedConversationAsync(otherDocumentId, "Other doc");
        var unscopedConversationId = await CreateConversationAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var list = await _appService.GetConversationListAsync(new GetChatConversationListInput
                {
                    DocumentId = documentId,
                    MaxResultCount = 50
                });

                list.TotalCount.ShouldBe(1);
                list.Items.Select(c => c.Id).ShouldContain(scopedConversationId);
                list.Items.Select(c => c.Id).ShouldNotContain(otherDocumentConversationId);
                list.Items.Select(c => c.Id).ShouldNotContain(unscopedConversationId);
            }
        });
    }

    // ── 4. Multi-turn history depth: 5 turns accumulate in the LLM payload ────
    // (Search-scope propagation moved to DocumentTextSearchAdapter_Tests — under
    //  the single MAF tool-calling path the substituted IChatClient does not
    //  invoke the search tool, so VectorSearchRequest can no longer be observed
    //  through the AppService surface.)

    [Fact]
    public async Task Should_Carry_Five_Turns_Of_History_To_LLM()
    {
        var conversationId = await CreateConversationAsync();

        // Track the messages passed to IChatClient per turn.
        var capturedTurns = new List<List<MEAI.ChatMessage>>();
        _chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<MEAI.ChatMessage>>(msgs =>
                    capturedTurns.Add(msgs.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var n = capturedTurns.Count;
                return Task.FromResult(
                    new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, $"answer-{n}")]));
            });

        for (var i = 1; i <= 5; i++)
        {
            var turn = i;
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message      = $"turn-{turn}",
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        }

        // All 5 turns must have reached the LLM.
        capturedTurns.Count.ShouldBe(5);
        capturedTurns[4].Count.ShouldBe(9);

        var fifthTurnTexts = capturedTurns[4].Select(m => m.Text).ToList();
        for (var i = 1; i <= 4; i++)
        {
            fifthTurnTexts.ShouldContain($"turn-{i}");
            fifthTurnTexts.ShouldContain($"answer-{i}");
        }
        fifthTurnTexts.ShouldContain("turn-5");

        var conv = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _conversationRepository.FindByIdWithMessagesAsync(conversationId, 50);
            }
        });
        conv!.Messages.Count.ShouldBe(10);
    }

    // ── 8. Validation: message content exceeding MaxMessageLength is rejected ──

    [Fact]
    public async Task Should_Reject_Message_Exceeding_MaxLength()
    {
        var conversationId = await CreateConversationAsync();

        await Should.ThrowAsync<AbpValidationException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OwnerUserId))
                {
                    await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                    {
                        Message      = new string('x', ChatConsts.MaxMessageLength + 1),
                        ClientTurnId = Guid.NewGuid()
                    });
                }
            });
        });
    }

    // ── 9. Guid.Empty ClientTurnId: documents current behavior ────────────────
    //
    //  [Required] on a non-nullable Guid struct does not reject Guid.Empty because
    //  the value is present (not null). Guid.Empty therefore passes validation and
    //  acts as an idempotency key — a second send with the same Guid.Empty replays
    //  the first turn rather than creating a new one. This test pins that behavior
    //  so that if a NotEmptyGuid validator or IValidatableObject guard is added later
    //  (to reject Guid.Empty explicitly), the test breaks and draws attention.

    [Fact]
    public async Task Should_Document_Guid_Empty_ClientTurnId_Idempotency_Behavior()
    {
        var conversationId = await CreateConversationAsync();

        // First send with Guid.Empty succeeds.
        ChatTurnResultDto first = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                first = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message      = "original message",
                    ClientTurnId = Guid.Empty
                });
            }
        });

        first.ShouldNotBeNull();
        first.UserMessageId.ShouldNotBe(Guid.Empty);

        // Second send with the same Guid.Empty is treated as an idempotent replay;
        // the same persisted message ids are returned without re-invoking the LLM.
        ChatTurnResultDto replay = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                replay = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message      = "original message",
                    ClientTurnId = Guid.Empty
                });
            }
        });

        replay.UserMessageId.ShouldBe(first.UserMessageId);
        replay.AssistantMessageId.ShouldBe(first.AssistantMessageId);

        // LLM was called exactly once despite two posts.
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    // ── 10. Delete: non-owner gets 404, not 403 ───────────────────────────────

    [Fact]
    public async Task Should_Return_404_When_Deleting_Conversation_Of_Other_User()
    {
        var conversationId = await CreateConversationAsync();

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                using (ChangeUser(OtherUserId))
                {
                    await _appService.DeleteConversationAsync(conversationId);
                }
            });
        });

        // Original conversation must still be intact after the rejected attempt.
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var detail = await _appService.GetConversationAsync(conversationId);
                detail.Id.ShouldBe(conversationId);
            }
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
                    Title = "E2E Test Conversation"
                });
                return dto.Id;
            }
        });

    private async Task<Guid> CreateDocumentScopedConversationAsync(Guid documentId, string title)
    {
        _documentRepository.GetAsync(documentId).Returns(CreateDocument(documentId));

        return await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = title,
                    DocumentId = documentId
                });
                return dto.Id;
            }
        });
    }

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            originalFileBlobName: $"test/{id:D}",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test",
                contentType: "application/pdf",
                contentHash: new string('a', 64),
                fileSize: 1,
                originalFileName: $"{id:D}.pdf"));
    }

    private IDisposable ChangeUser(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(AbpClaimTypes.UserId, userId.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return _principalAccessor.Change(principal);
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

    private void SetupDefaultEmbedding()
    {
        var vector     = new float[] { 0.1f, 0.2f, 0.3f };
        var embedding  = new Embedding<float>(vector);
        var embeddings = new GeneratedEmbeddings<Embedding<float>>([embedding]);
        _embeddingGenerator
            .GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(embeddings);
    }

    private void SetupDefaultKnowledgeIndex()
    {
        _knowledgeIndex
            .SearchAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());
    }
}

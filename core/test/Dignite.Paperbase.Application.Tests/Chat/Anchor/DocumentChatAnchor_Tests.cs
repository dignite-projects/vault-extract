using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Chat.Telemetry;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp.Auditing;
using Volo.Abp.Security.Claims;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Issue #100 — guards for the anchor-document handling in
/// <see cref="DocumentChatAppService.BuildAnchorContextAsync"/>. The anchor must:
/// <list type="bullet">
///   <item>Include only structured identifiers (id + typeCode), <strong>never</strong> the
///         user-controlled <c>Document.Title</c> (prompt-injection vector — see
///         <c>.claude/rules/doc-chat-anti-patterns.md</c> reverse example E #1).</item>
///   <item>Degrade gracefully when the anchor document cannot be resolved
///         (deleted / cross-tenant / caller lost <c>Documents.Default</c>) — turn proceeds
///         without the anchor; never throws (reverse example E #2/#3).</item>
///   <item>Surface degradation via the <c>AnchorResolutionFailed</c> audit field so
///         operators can observe permission drift at scale.</item>
/// </list>
/// </summary>
public class DocumentChatAnchor_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly IAuditingManager _auditingManager;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // The Title is the prompt-injection vector this test class proves we don't leak.
    // Anything from "Ignore previous" / "<script>" / etc. would do; this string is
    // both unique enough to grep for and shaped like an injection attempt.
    private const string SensitiveDocumentTitle = "ANCHOR_TITLE_DO_NOT_LEAK_OR_OBEY";

    // The first-line sentinel BuildAnchorContextAsync writes when an anchor IS
    // injected. Asserting on this avoids false positives from PromptBoundary.BoundaryRule
    // (which legitimately mentions the literal "<anchor>" tag name as a protected zone).
    private const string AnchorBlockSentinel = "User opened this conversation from a document detail page";

    public DocumentChatAnchor_Tests()
    {
        _appService = GetRequiredService<IDocumentChatAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _knowledgeIndex = GetRequiredService<IDocumentKnowledgeIndex>();
        _chatClient = GetRequiredService<IChatClient>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _auditingManager = GetRequiredService<IAuditingManager>();

        SetupDefaultEmbedding();
        SetupDefaultKnowledgeIndex();
        SetupDefaultChatClient();
    }

    [Fact]
    public async Task Anchor_Block_Includes_Id_And_TypeCode_But_Never_Title()
    {
        var anchorId = Guid.NewGuid();
        const string anchorTypeCode = "contract.general";
        SetupAnchorDocument(anchorId, anchorTypeCode, SensitiveDocumentTitle);

        ChatOptions? captured = null;
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => captured ??= o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new MEAI.ChatMessage(ChatRole.Assistant, "ok")])));

        var conversationId = await CreateConversationAsync(anchorId);

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "what is this?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        captured.ShouldNotBeNull();
        var instructions = captured!.Instructions;
        instructions.ShouldNotBeNullOrEmpty();

        instructions.ShouldContain(anchorId.ToString());
        instructions.ShouldContain(anchorTypeCode);
        instructions.ShouldContain("<anchor>");

        // SECURITY: Title is set by the uploader / generation workflow and is the
        // prompt-injection vector reverse example E #1 forbids in the anchor block.
        instructions.ShouldNotContain(SensitiveDocumentTitle);
    }

    [Fact]
    public async Task Anchor_Block_Absent_When_Conversation_Has_No_DocumentId()
    {
        ChatOptions? captured = null;
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => captured ??= o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new MEAI.ChatMessage(ChatRole.Assistant, "ok")])));

        var conversationId = await CreateConversationAsync(documentId: null);

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "anything",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        captured.ShouldNotBeNull();
        // The boundary rule itself names "<anchor>" as one of its protected tags, so a
        // raw substring search for the tag would always match. Look for the actual
        // anchor-block sentinel that BuildAnchorContextAsync injects.
        captured!.Instructions.ShouldNotContain(AnchorBlockSentinel);
    }

    [Fact]
    public async Task Anchor_Resolution_Failure_Degrades_Gracefully_When_Document_Missing()
    {
        // Conversation was created with a valid anchor (CreateConversationAsync uses
        // GetAsync to validate) — between turns the document was hard-deleted, so
        // FindAsync returns null. Issue #100 reverse example E #2 mandates the turn
        // proceeds without the anchor (no exception, no anchor block, telemetry signal).
        var anchorId = Guid.NewGuid();
        SetupAnchorDocument(anchorId, "contract.general", SensitiveDocumentTitle);
        var conversationId = await CreateConversationAsync(anchorId);

        // Now simulate the document going away before the next turn.
        _documentRepository
            .FindAsync(anchorId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        ChatOptions? captured = null;
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => captured ??= o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new MEAI.ChatMessage(ChatRole.Assistant, "ok")])));

        using var auditScope = _auditingManager.BeginScope();
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                // Must succeed — never throw EntityNotFoundException for a missing anchor.
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "history still works",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        captured.ShouldNotBeNull();
        // The boundary rule itself names "<anchor>" as one of its protected tags, so a
        // raw substring search for the tag would always match. Look for the actual
        // anchor-block sentinel that BuildAnchorContextAsync injects.
        captured!.Instructions.ShouldNotContain(AnchorBlockSentinel);
        captured.Instructions.ShouldNotContain(anchorId.ToString());

        var turn = _auditingManager.Current!.Log.ExtraProperties[
                DocumentChatTelemetryRecorder.AuditTurnPropertyName]
            .ShouldBeOfType<DocumentChatTurnAuditEntry>();
        turn.AnchorResolutionFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task Anchor_Resolution_Failure_Degrades_When_Document_Belongs_To_Another_Tenant()
    {
        // Defense-in-depth: even if the ABP DataFilter is bypassed (e.g. a future
        // background-job code path forgets to enable it), an anchor whose TenantId
        // mismatches the conversation's TenantId must be dropped, not leaked.
        var anchorId = Guid.NewGuid();
        var conversationTenantId = (Guid?)null; // host tenant in this test base
        var foreignTenantId = Guid.NewGuid();

        // CreateConversation needs GetAsync to succeed (uses the same-tenant document)
        SetupAnchorDocument(anchorId, "contract.general", SensitiveDocumentTitle, tenantId: conversationTenantId);
        var conversationId = await CreateConversationAsync(anchorId);

        // Now FindAsync returns a document that pretends to belong to a different tenant.
        var crossTenantDoc = CreateDocument(anchorId, "contract.general", SensitiveDocumentTitle, foreignTenantId);
        _documentRepository
            .FindAsync(anchorId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(crossTenantDoc);

        ChatOptions? captured = null;
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => captured ??= o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new MEAI.ChatMessage(ChatRole.Assistant, "ok")])));

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "what is this?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        captured.ShouldNotBeNull();
        // Cross-tenant anchor must NOT be injected — the closure-captured tenant is
        // the only authority that decides what a turn is allowed to see.
        // The boundary rule itself names "<anchor>" as one of its protected tags, so a
        // raw substring search for the tag would always match. Look for the actual
        // anchor-block sentinel that BuildAnchorContextAsync injects.
        captured!.Instructions.ShouldNotContain(AnchorBlockSentinel);
        captured.Instructions.ShouldNotContain(anchorId.ToString());
        captured.Instructions.ShouldNotContain(SensitiveDocumentTitle);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateConversationAsync(Guid? documentId)
        => await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Anchor Test",
                    DocumentId = documentId
                });
                return dto.Id;
            }
        });

    private void SetupAnchorDocument(Guid id, string typeCode, string title, Guid? tenantId = null)
    {
        var doc = CreateDocument(id, typeCode, title, tenantId);
        // GetAsync is what CreateConversationAsync uses to validate existence at create time.
        _documentRepository.GetAsync(id).Returns(doc);
        // FindAsync is what BuildAnchorContextAsync uses on every turn.
        _documentRepository
            .FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument(Guid id, string typeCode, string title, Guid? tenantId)
    {
        var doc = new Document(
            id,
            tenantId: tenantId,
            originalFileBlobName: $"test/{id:D}",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test",
                contentType: "application/pdf",
                contentHash: new string('a', 64),
                fileSize: 1,
                originalFileName: $"{id:D}.pdf"));

        // Wire in the typeCode + title that the production aggregate would set after
        // classification / title-generation workflows. The production aggregate
        // exposes setters via domain methods; for test ergonomics we go via reflection
        // — Document is sealed against external mutation deliberately.
        SetPrivate(doc, nameof(Document.DocumentTypeCode), typeCode);
        SetPrivate(doc, nameof(Document.Title), title);
        return doc;
    }

    private static void SetPrivate(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop == null)
            throw new InvalidOperationException($"Property {propertyName} not found on {target.GetType().FullName}.");
        prop.SetValue(target, value);
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

    private void SetupDefaultChatClient()
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse([new MEAI.ChatMessage(ChatRole.Assistant, "stub answer")])));
    }
}

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
using Volo.Abp.Security.Claims;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Issue #116: streaming-path guard that the AppService surfaces MAF's
/// <see cref="MEAI.FunctionCallContent"/> / <see cref="MEAI.FunctionResultContent"/>
/// updates as <see cref="ChatTurnDeltaKind.ToolCallStarted"/> /
/// <see cref="ChatTurnDeltaKind.ToolCallCompleted"/> events on the SSE channel —
/// so the user sees activity instead of a black screen during multi-tool reasoning.
/// </summary>
public class DocumentChatToolProgressStreaming_Tests
    : PaperbaseApplicationTestBase<DocumentChatAppServiceTestModule>
{
    private readonly IDocumentChatAppService _appService;
    private readonly IChatClient _chatClient;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public DocumentChatToolProgressStreaming_Tests()
    {
        _appService = GetRequiredService<IDocumentChatAppService>();
        _chatClient = GetRequiredService<IChatClient>();
        _knowledgeIndex = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        SetupDefaultEmbedding();
        _knowledgeIndex.SearchAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());
    }

    [Fact]
    public async Task Streaming_Surfaces_ToolCallStarted_And_ToolCallCompleted_Events()
    {
        // Script the IChatClient stream to walk through:
        //   1) FunctionCallContent (search_paperbase_documents) → 2) FunctionResultContent
        //   3) FunctionCallContent (unknown_tool, exercises the fallback label)
        //   4) FunctionResultContent (failure)
        //   5–6) text chunks → end
        // The test deliberately uses Substitute.For<IChatClient> directly (no
        // FunctionInvokingChatClient wrap) — that lets us drive the exact sequence
        // of updates the AppService translates without having to register real tool
        // implementations or rely on the function-invocation loop.
        var searchCallId = "call-search-1";
        var unknownCallId = "call-unknown-2";

        _chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ScriptStream(searchCallId, unknownCallId));

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

        // Expected order: Started/Completed/Started/Completed/PartialText × 2/Done
        deltas.Select(d => d.Kind).ToList().ShouldBe(new[]
        {
            ChatTurnDeltaKind.ToolCallStarted,
            ChatTurnDeltaKind.ToolCallCompleted,
            ChatTurnDeltaKind.ToolCallStarted,
            ChatTurnDeltaKind.ToolCallCompleted,
            ChatTurnDeltaKind.PartialText,
            ChatTurnDeltaKind.PartialText,
            ChatTurnDeltaKind.Done
        });

        // First Started — the registered search_paperbase_documents tool: describer
        // should resolve to the structured "正在跨全库向量检索…" label (no doc-ids /
        // type were supplied in the scripted call args).
        var firstStarted = deltas[0];
        firstStarted.ToolName.ShouldBe(ChatConsts.SearchPaperbaseDocumentsToolName);
        firstStarted.ToolCallId.ShouldBe(searchCallId);
        firstStarted.ProgressDescription.ShouldBe("正在跨全库向量检索…");

        // First Completed — correlates by ToolCallId, ElapsedMs populated, success.
        var firstCompleted = deltas[1];
        firstCompleted.ToolCallId.ShouldBe(searchCallId);
        firstCompleted.ToolName.ShouldBe(ChatConsts.SearchPaperbaseDocumentsToolName);
        firstCompleted.ToolCallSucceeded.ShouldBe(true);
        firstCompleted.ElapsedMs.ShouldNotBeNull();

        // Second Started — unknown_tool isn't in the agent's tool list; falls back
        // to the generic "正在执行 {ToolName}…" label.
        var secondStarted = deltas[2];
        secondStarted.ToolName.ShouldBe("unknown_tool");
        secondStarted.ToolCallId.ShouldBe(unknownCallId);
        secondStarted.ProgressDescription.ShouldBe("正在执行 unknown_tool…");

        // Second Completed — failure path; ToolCallSucceeded must be false.
        var secondCompleted = deltas[3];
        secondCompleted.ToolCallId.ShouldBe(unknownCallId);
        secondCompleted.ToolCallSucceeded.ShouldBe(false);

        // Text chunks survive verbatim.
        deltas[4].Text.ShouldBe("Hello");
        deltas[5].Text.ShouldBe(" World");
    }

    [Fact]
    public async Task Streaming_Without_Tool_Calls_Yields_Only_PartialText_And_Done()
    {
        // Regression guard: if the model never invokes a tool (the Issue #100 anchor /
        // pure-knowledge case), the new code path must NOT fabricate ToolCall events.
        _chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => YieldText("plain ", "answer"));

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

        deltas.ShouldNotContain(d =>
            d.Kind == ChatTurnDeltaKind.ToolCallStarted
            || d.Kind == ChatTurnDeltaKind.ToolCallCompleted);
        deltas.Count(d => d.Kind == ChatTurnDeltaKind.PartialText).ShouldBe(2);
        deltas[^1].Kind.ShouldBe(ChatTurnDeltaKind.Done);
    }

    private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> ScriptStream(
        string searchCallId,
        string unknownCallId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Tool 1: registered tool (search_paperbase_documents). No documentIds / typeCode
        // in arguments → describer returns the "全库向量检索" label.
        yield return UpdateWithContent(new MEAI.FunctionCallContent(
            callId: searchCallId,
            name: ChatConsts.SearchPaperbaseDocumentsToolName,
            arguments: new Dictionary<string, object?> { ["query"] = "anything" }));

        await Task.Yield();

        yield return UpdateWithContent(new MEAI.FunctionResultContent(
            callId: searchCallId,
            result: "{\"results\":[]}"));

        // Tool 2: tool not registered with the agent → AppService falls back to generic label.
        yield return UpdateWithContent(new MEAI.FunctionCallContent(
            callId: unknownCallId,
            name: "unknown_tool",
            arguments: new Dictionary<string, object?>()));

        await Task.Yield();

        // Failure path: include an Exception so ToolCallSucceeded comes through false.
        yield return UpdateWithContent(new MEAI.FunctionResultContent(
            callId: unknownCallId,
            result: null)
        {
            Exception = new InvalidOperationException("scripted failure")
        });

        // Final text chunks the assistant streams after tool work is done.
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "Hello");
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, " World");
    }

    private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> YieldText(
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
        }
    }

    private static MEAI.ChatResponseUpdate UpdateWithContent(MEAI.AIContent content)
        => new()
        {
            Role = MEAI.ChatRole.Assistant,
            Contents = new List<MEAI.AIContent> { content }
        };

    private async Task<Guid> CreateConversationAsync()
        => await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Tool-progress streaming test"
                });
                return dto.Id;
            }
        });

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
}

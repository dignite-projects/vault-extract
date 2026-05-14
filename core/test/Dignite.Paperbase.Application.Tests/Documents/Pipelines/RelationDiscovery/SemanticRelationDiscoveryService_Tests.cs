using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Dignite.Paperbase.Tests.Vectors;
using Dignite.Paperbase.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class SemanticRelationDiscoveryServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRelationRepository>());
        context.Services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        var fakeCollection = new FakeDocumentChunkCollection();
        context.Services.AddSingleton(fakeCollection);
        context.Services.AddSingleton<DocumentChunkCollectionProvider>(
            new FakeDocumentChunkCollectionProvider(fakeCollection));

        // Replace RelationInferenceAgent entirely so tests don't need a real IChatClient.
        // Construct with cheap substitutes for its dependencies.
        context.Services.AddSingleton(Substitute.For<RelationInferenceAgent>(
            Substitute.For<IChatClient>(),
            Options.Create(new PaperbaseAIBehaviorOptions())));

        // Default: enable semantic discovery so most tests exercise the full path.
        // The "disabled" test overrides this via direct field manipulation on the resolved options.
        context.Services.Configure<PaperbaseAIBehaviorOptions>(opts =>
        {
            opts.EnableSemanticRelationDiscovery = true;
            opts.SemanticRelationDiscoveryTopK = 5;
            opts.SemanticRelationDiscoveryMinScore = 0.65;
            opts.SemanticRelationDiscoveryConfidenceThreshold = 0.7;
        });
    }
}

public class SemanticRelationDiscoveryService_Tests
    : PaperbaseApplicationTestBase<SemanticRelationDiscoveryServiceTestModule>
{
    private readonly SemanticRelationDiscoveryService _service;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly FakeDocumentChunkCollection _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly RelationInferenceAgent _inferenceAgent;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    public SemanticRelationDiscoveryService_Tests()
    {
        _service = GetRequiredService<SemanticRelationDiscoveryService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        _collection = GetRequiredService<FakeDocumentChunkCollection>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _inferenceAgent = GetRequiredService<RelationInferenceAgent>();
        _aiOptions = GetRequiredService<IOptions<PaperbaseAIBehaviorOptions>>().Value;
        _collection.Reset();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Short_Circuit_When_Disabled()
    {
        _aiOptions.EnableSemanticRelationDiscovery = false;
        var sourceId = Guid.NewGuid();

        var created = await _service.DiscoverAsync(sourceId);

        created.Relations.ShouldBeEmpty();
        // Short-circuit means NO IO at all — not even a document load.
        await _documentRepository.DidNotReceive().FindAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Reject_Empty_Source_Id()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _service.DiscoverAsync(Guid.Empty));
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Source_Not_Found()
    {
        var sourceId = Guid.NewGuid();
        _documentRepository.FindAsync(sourceId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var created = await _service.DiscoverAsync(sourceId);

        created.Relations.ShouldBeEmpty();
        await _embeddingGenerator.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Source_Has_No_Markdown()
    {
        var source = CreateDocument(markdown: null);
        SetupSource(source);

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Vector_Search_Yields_No_Candidates()
    {
        var source = CreateDocument(markdown: "合同内容");
        SetupSource(source);
        SetupEmbedding();
        // Empty staged batch → empty IAsyncEnumerable → 0 candidates.

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Filter_Self_From_Vector_Results()
    {
        // The production filter already excludes self (DocumentId != sourceDocKey).
        // Stage a hit that names the source — the filter compiled against
        // DocumentChunkRecord would normally reject it, but we still exercise the
        // service-layer guard by staging it directly into the result queue.
        var source = CreateDocument(markdown: "合同内容");
        SetupSource(source);
        SetupEmbedding();
        StageVectorHit(source.Id, score: 0.95);

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Already_Linked_Candidates()
    {
        var source = CreateDocument(markdown: "合同内容");
        var linkedPeer = CreateDocument(markdown: "已关联文档");
        SetupSource(source);
        SetupEmbedding();
        StageVectorHit(linkedPeer.Id, score: 0.85);
        // Pre-existing relation → must skip.
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                source.Id, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { linkedPeer.Id });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_When_LLM_Says_Not_Related()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "无关文档");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        StageVectorHit(candidate.Id, score: 0.7);
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult { IsRelated = false, Confidence = 0.2 });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        await _relationRepository.DidNotReceive().InsertAsync(
            Arg.Any<DocumentRelation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_When_Confidence_Below_Threshold()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "弱相关文档");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        StageVectorHit(candidate.Id, score: 0.7);
        SetupNoExistingRelations(source.Id);

        // LLM says related but confidence is below threshold (0.7).
        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 0.5,
                Description = "可能有关"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
    }

    [Fact]
    public async Task TenantIsolation_Recall_Filter_Excludes_Other_Tenant_Candidates()
    {
        // End-to-end guard: stage one in-tenant candidate + one cross-tenant hit
        // in the same staged batch. The fake collection applies the production
        // filter at dequeue time. The recall filter must scope to source.TenantId;
        // the cross-tenant hit must never become a candidate.
        var source = CreateDocument(markdown: "tenant-host source");
        var inTenantCandidate = CreateDocument(markdown: "in-tenant candidate");
        var otherTenantId = Guid.NewGuid();
        var otherTenantDocId = Guid.NewGuid();

        SetupSource(source);
        SetupCandidate(inTenantCandidate);
        SetupEmbedding();
        SetupNoExistingRelations(source.Id);

        _collection.StagedSearchResults.Enqueue(new[]
        {
            BuildHit(inTenantCandidate.Id, score: 0.9),
            new VectorSearchResult<DocumentChunkRecord>(
                new DocumentChunkRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = DocumentChunkPayloadEncoding.EncodeTenantId(otherTenantId),
                    DocumentId = DocumentChunkPayloadEncoding.EncodeDocumentId(otherTenantDocId),
                    ChunkIndex = 0,
                    Text = "other-tenant-chunk",
                },
                score: 0.88),
        });

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 0.85,
                Description = "in-tenant match",
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.Count.ShouldBe(1);
        created.Relations.Single().TargetDocumentId.ShouldBe(inTenantCandidate.Id);
        // Cross-tenant doc never enters candidate list → never reaches LLM evaluation.
        await _inferenceAgent.Received(1).EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Create_AiSuggested_When_LLM_Confirms()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        StageVectorHit(candidate.Id, score: 0.85);
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 0.85,
                Description = "该发票对应该合同"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.Count.ShouldBe(1);
        var rel = created.Relations.Single();
        rel.SourceDocumentId.ShouldBe(source.Id);
        rel.TargetDocumentId.ShouldBe(candidate.Id);
        rel.Source.ShouldBe(RelationSource.AiSuggested);
        rel.Confidence.ShouldBe(0.85);
        rel.Description.ShouldBe("该发票对应该合同");
        // autoSave: true — LLM/DB rule (no UoW during external work).
        await _relationRepository.Received(1).InsertAsync(
            Arg.Any<DocumentRelation>(), autoSave: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Continue_When_LLM_Throws_For_One_Candidate()
    {
        var source = CreateDocument(markdown: "合同内容");
        var failCandidate = CreateDocument(markdown: "LLM 调用失败");
        var goodCandidate = CreateDocument(markdown: "正常候选");
        SetupSource(source);
        SetupCandidate(failCandidate);
        SetupCandidate(goodCandidate);
        SetupEmbedding();
        _collection.StagedSearchResults.Enqueue(new[]
        {
            BuildHit(failCandidate.Id, score: 0.85),
            BuildHit(goodCandidate.Id, score: 0.8),
        });
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Is<DocumentSnapshot>(s => s.Markdown == "合同内容"),
                Arg.Is<DocumentSnapshot>(c => c.Markdown == "LLM 调用失败"),
                Arg.Any<CancellationToken>())
            .Returns<RelationInferenceResult>(_ => throw new InvalidOperationException("LLM provider down"));

        _inferenceAgent.EvaluateAsync(
                Arg.Is<DocumentSnapshot>(s => s.Markdown == "合同内容"),
                Arg.Is<DocumentSnapshot>(c => c.Markdown == "正常候选"),
                Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true, Confidence = 0.8, Description = "match"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.Count.ShouldBe(1);
        created.Relations.Single().TargetDocumentId.ShouldBe(goodCandidate.Id);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Run_LLM_And_Vector_Search_Without_Ambient_UoW()
    {
        // .claude/rules/background-jobs.md § Tests: regression guard against future code
        // accidentally wrapping L3 in an outer UoW that holds a DB connection during LLM calls.
        var uowManager = GetRequiredService<Volo.Abp.Uow.IUnitOfWorkManager>();
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupNoExistingRelations(source.Id);

        // Assert NO ambient UoW at the embedding boundary.
        _embeddingGenerator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uowManager.Current.ShouldBeNull();
                return new GeneratedEmbeddings<Embedding<float>>(new[]
                {
                    new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f })
                });
            });

        // Stage one vector hit + assert NO ambient UoW when the fake's SearchAsync runs.
        _collection.StagedSearchResults.Enqueue(new[] { BuildHit(candidate.Id, score: 0.9) });
        _collection.OnGetByFilterInvoked = () => uowManager.Current.ShouldBeNull();

        // Assert NO ambient UoW at the LLM evaluation boundary.
        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uowManager.Current.ShouldBeNull();
                return new RelationInferenceResult
                {
                    IsRelated = true, Confidence = 0.85, Description = "match"
                };
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.Count.ShouldBe(1);
        _collection.SearchCalls.ShouldBe(1);
        await _inferenceAgent.Received(1).EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Reject_Non_Finite_Confidence_From_LLM()
    {
        // R3-followup [2nd-round review] regression guard: Math.Clamp(NaN, 0, 1) returns NaN
        // (NaN compares false against both bounds). Subsequent `NaN < threshold` is also false,
        // so without explicit NaN handling, NaN flows into DocumentRelation and persists.
        // ASP.NET Core's default JsonSerializer throws on NaN → poisons the relation list
        // GET endpoint with HTTP 500. Service must Reject non-finite confidence up front.
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        StageVectorHit(candidate.Id, score: 0.9);
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = double.NaN,   // LLM drift: non-finite
                Description = "match"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        // No DocumentRelation insert attempted (would have persisted NaN before the fix).
        await _relationRepository.DidNotReceive().InsertAsync(
            Arg.Any<DocumentRelation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Clamp_Out_Of_Range_Confidence_From_LLM()
    {
        // R3 regression guard: LLM occasionally returns confidence > 1 or < 0 despite the
        // prompt constraint. Without clamp, `new DocumentRelation(confidence: 1.5)` throws
        // in ValidateConfidence → caught by outer `catch (Exception)` → silently logged as
        // Error → user never sees this AiSuggested relation. Clamp + warn keeps the signal
        // visible while preserving the aggregate invariant.
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        StageVectorHit(candidate.Id, score: 0.9);
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 1.5,   // LLM drift: out of [0, 1]
                Description = "match"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.Count.ShouldBe(1);
        created.Relations.Single().Confidence.ShouldBe(1.0);   // Clamped, not raw 1.5
    }

    [Fact]
    public async Task DiscoverAsync_Should_Short_Circuit_After_Consecutive_LLM_Failures()
    {
        // R4 regression guard: when LLM provider is genuinely down (not a single transient
        // hiccup), per-candidate try/catch alone would let foreach burn the full per-call
        // timeout on every candidate. Consecutive-failure cutoff (default 2) bails out
        // after the second contiguous failure, preventing Hangfire worker starvation.
        _aiOptions.SemanticRelationDiscoveryConsecutiveFailureCutoff = 2;

        var source = CreateDocument(markdown: "合同内容");
        var c1 = CreateDocument(markdown: "候选1");
        var c2 = CreateDocument(markdown: "候选2");
        var c3 = CreateDocument(markdown: "候选3");
        SetupSource(source);
        SetupCandidate(c1);
        SetupCandidate(c2);
        SetupCandidate(c3);
        SetupEmbedding();
        _collection.StagedSearchResults.Enqueue(new[]
        {
            BuildHit(c1.Id, score: 0.9),
            BuildHit(c2.Id, score: 0.85),
            BuildHit(c3.Id, score: 0.8),
        });
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns<RelationInferenceResult>(_ => throw new InvalidOperationException("LLM provider down"));

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        // Exactly 2 candidates attempted before the circuit breaks; c3 never evaluated.
        await _inferenceAgent.Received(2).EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Treat_Dismissed_Tombstones_As_Already_Linked()
    {
        // R2 regression guard: when a user dismisses an AI suggestion, the row is soft-deleted
        // (IsDeleted=true). L3 must still treat that document as "already linked" so the same
        // pair is not re-suggested next run. The contract is: SemanticRelationDiscoveryService
        // calls GetLinkedPeerDocumentIdsAsync with includeDismissed=true so the repo bypasses
        // the soft-delete filter — this test verifies that contract.
        var source = CreateDocument(markdown: "合同内容");
        var dismissedPeer = CreateDocument(markdown: "用户曾驳回的相似文档");
        SetupSource(source);
        SetupEmbedding();
        StageVectorHit(dismissedPeer.Id, score: 0.9);

        // Substitute returns the dismissed peer ONLY when includeDismissed=true is requested.
        // If production code drops the flag (regression), the substitute returns null/empty
        // for the wrong overload and the test fails — exactly the safety net we want.
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                source.Id, Arg.Any<Guid?>(), includeDismissed: true, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { dismissedPeer.Id });

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
        // Verify the call was made with includeDismissed=true (R2 contract).
        await _relationRepository.Received(1).GetLinkedPeerDocumentIdsAsync(
            source.Id, Arg.Any<Guid?>(), includeDismissed: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Candidate_With_No_Markdown()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidateWithoutMarkdown = CreateDocument(markdown: null);
        SetupSource(source);
        SetupCandidate(candidateWithoutMarkdown);
        SetupEmbedding();
        StageVectorHit(candidateWithoutMarkdown.Id, score: 0.9);
        SetupNoExistingRelations(source.Id);

        var created = await _service.DiscoverAsync(source.Id);

        created.Relations.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupSource(Document doc)
    {
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void SetupCandidate(Document doc)
    {
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void SetupNoExistingRelations(Guid sourceId)
    {
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
    }

    private void SetupEmbedding()
    {
        _embeddingGenerator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(new[]
            {
                new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f })
            }));
    }

    private void StageVectorHit(Guid documentId, double score)
    {
        _collection.StagedSearchResults.Enqueue(new[] { BuildHit(documentId, score) });
    }

    private static VectorSearchResult<DocumentChunkRecord> BuildHit(Guid documentId, double score)
    {
        var record = new DocumentChunkRecord
        {
            Id = Guid.NewGuid(),
            TenantId = DocumentChunkPayloadEncoding.HostTenantId,
            DocumentId = DocumentChunkPayloadEncoding.EncodeDocumentId(documentId),
            ChunkIndex = 0,
            Text = "chunk",
        };
        return new VectorSearchResult<DocumentChunkRecord>(record, score);
    }

    private static Document CreateDocument(string? markdown)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        if (markdown != null)
        {
            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, new object[] { markdown });
        }

        return doc;
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class RelationDiscoveryBackgroundJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRelationRepository>());

        // Replace RelationDiscoveryService entirely — this test only verifies the JOB's
        // PipelineRun lifecycle wiring, not the discovery logic (covered separately by
        // RelationDiscoveryService_Tests).
        // Issue #121: L2 ctor now takes IDocumentRepository instead of ICurrentTenant.
        context.Services.AddSingleton(Substitute.For<RelationDiscoveryService>(
            Array.Empty<IDocumentIdentifierProvider>(),
            Substitute.For<IDocumentRelationRepository>(),
            Substitute.For<IDocumentRepository>()));

        // Same treatment for L3 — substitute so this test stays focused on the job's lifecycle
        // and L2 → L3 fallback chaining; L3's own logic is covered by SemanticRelationDiscoveryService_Tests.
        context.Services.AddSingleton(Substitute.For<SemanticRelationDiscoveryService>(
            Substitute.For<IDocumentRepository>(),
            Substitute.For<IDocumentRelationRepository>(),
            Substitute.For<IDocumentKnowledgeIndex>(),
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            Substitute.For<RelationInferenceAgent>(
                Substitute.For<IChatClient>(),
                Options.Create(new PaperbaseAIBehaviorOptions())),
            new RelationDiscoveryTelemetryRecorder(NullLogger<RelationDiscoveryTelemetryRecorder>.Instance),
            Options.Create(new PaperbaseAIBehaviorOptions())));
    }
}

/// <summary>
/// Verifies <see cref="RelationDiscoveryBackgroundJob"/>'s short-UoW lifecycle:
/// PipelineRun is created → Running → Succeeded/Failed in three separate UoWs;
/// service throwing surfaces as Failed without crashing the job.
/// </summary>
public class RelationDiscoveryBackgroundJob_Tests
    : PaperbaseApplicationTestBase<RelationDiscoveryBackgroundJobTestModule>
{
    private readonly RelationDiscoveryBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly RelationDiscoveryService _discoveryService;
    private readonly SemanticRelationDiscoveryService _semanticDiscoveryService;

    public RelationDiscoveryBackgroundJob_Tests()
    {
        _job = GetRequiredService<RelationDiscoveryBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _discoveryService = GetRequiredService<RelationDiscoveryService>();
        _semanticDiscoveryService = GetRequiredService<SemanticRelationDiscoveryService>();

        // Default: L3 fallback returns empty (most lifecycle tests don't care about L3 results).
        // Tests exercising the fallback path override this explicitly.
        _semanticDiscoveryService
            .DiscoverAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());
    }

    [Fact]
    public async Task ExecuteAsync_Should_Mark_Run_Succeeded_When_Discovery_Returns_Successfully()
    {
        var doc = CreateDocument();
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        var run = doc.GetLatestRun(PaperbasePipelines.RelationDiscovery);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Mark_Run_Failed_When_Discovery_Throws()
    {
        var doc = CreateDocument();
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DocumentRelation>>(_ => throw new InvalidOperationException("DB connection lost"));

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        var run = doc.GetLatestRun(PaperbasePipelines.RelationDiscovery);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Failed);
        run.StatusMessage.ShouldBe("DB connection lost");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Drop_Job_Silently_When_Document_Not_Found()
    {
        // Document hard-deleted between event publish and job pickup — drop without throwing.
        var documentId = Guid.NewGuid();
        _documentRepository
            .FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = documentId });

        await _discoveryService.DidNotReceive().DiscoverAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Should_Fall_Back_To_L3_When_L2_Returns_Empty()
    {
        // L2 finds nothing → background job invokes L3. L3 returns one relation.
        var doc = CreateDocument();
        SetupDocumentRepository(doc);

        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());

        var l3Relation = new DocumentRelation(
            Guid.NewGuid(), null,
            sourceDocumentId: doc.Id,
            targetDocumentId: Guid.NewGuid(),
            description: "semantic match",
            source: RelationSource.AiSuggested,
            confidence: 0.85);

        _semanticDiscoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation> { l3Relation });

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        await _semanticDiscoveryService.Received(1).DiscoverAsync(doc.Id, Arg.Any<CancellationToken>());
        var run = doc.GetLatestRun(PaperbasePipelines.RelationDiscovery);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Skip_L3_When_L2_Found_Relations()
    {
        // L2 found ≥1 relation → L3 (expensive LLM) must NOT be invoked.
        var doc = CreateDocument();
        SetupDocumentRepository(doc);

        var l2Relation = new DocumentRelation(
            Guid.NewGuid(), null,
            sourceDocumentId: doc.Id,
            targetDocumentId: Guid.NewGuid(),
            description: "structured match",
            source: RelationSource.AiSuggested,
            confidence: 0.95);

        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation> { l2Relation });

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        await _semanticDiscoveryService.DidNotReceive().DiscoverAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Should_Reuse_Pending_Run_When_PipelineRunId_Provided()
    {
        // The scheduler creates a Pending run before enqueue and passes its id in args.
        // Job must pick it up rather than start a fresh one.
        var doc = CreateDocument();
        var pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        var pendingRun = await pipelineRunManager.QueueAsync(doc, PaperbasePipelines.RelationDiscovery);
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs
        {
            DocumentId = doc.Id,
            PipelineRunId = pendingRun.Id
        });

        pendingRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
        pendingRun.AttemptNumber.ShouldBe(1);
        doc.PipelineRuns.Count.ShouldBe(1);   // No duplicate run created
    }

    private void SetupDocumentRepository(Document doc)
    {
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _documentRepository
            .GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(), tenantId: null,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

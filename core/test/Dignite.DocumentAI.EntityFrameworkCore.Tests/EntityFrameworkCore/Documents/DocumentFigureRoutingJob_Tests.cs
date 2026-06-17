using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.DocumentTypes;
using Dignite.DocumentAI.Documents.Figures;
using Dignite.DocumentAI.Documents.Pipelines;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
using Dignite.DocumentAI.Documents.Pipelines.Routing;
using Dignite.DocumentAI.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class DocumentFigureRoutingJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // Gate seam: a partial substitute of the classification workflow whose RunAsync each test stubs to a
        // chosen outcome — no real LLM call (mirrors DocumentClassificationBackgroundJob_Tests).
        var workflow = Substitute.ForPartsOf<DocumentClassificationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new DocumentAIBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);
    }
}

public class DocumentFigureRoutingJob_Tests : DocumentAITestBase<DocumentFigureRoutingJobTestModule>
{
    private const string TypeCode = "invoice.general";
    private const double Threshold = 0.75;

    private readonly DocumentFigureRoutingJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentFigure, Guid> _figureRepository;
    private readonly IRepository<DocumentType, Guid> _typeRepository;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentFigureRoutingJob_Tests()
    {
        _job = GetRequiredService<DocumentFigureRoutingJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _figureRepository = GetRequiredService<IRepository<DocumentFigure, Guid>>();
        _typeRepository = GetRequiredService<IRepository<DocumentType, Guid>>();
        _blobContainer = GetRequiredService<IBlobContainer<DocumentAIDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _workflow = GetRequiredService<DocumentClassificationWorkflow>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Confident_Figure_Spawns_Derived_Document()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, contentHash, cropBlobName) = await ArrangeAsync(sourceId, withType: true);
        StubCropBlob(cropBlobName);
        StubGate(TypeCode, 0.92);

        await _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId });

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = (await _documentRepository.GetListAsync(d => d.OriginDocumentId == sourceId)).SingleOrDefault();
            derived.ShouldNotBeNull();
            derived!.OriginConstituentKey.ShouldBe(contentHash);
            derived.FileOrigin.ContentHash.ShouldBe(contentHash);
            derived.FileOrigin.BlobName.ShouldNotBe(cropBlobName); // copied to an independent blob
            derived.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing); // text-extraction queued

            var figure = await _figureRepository.GetAsync(figureId);
            figure.Status.ShouldBe(DocumentFigureStatus.Spawned);
            figure.RoutedDocumentId.ShouldBe(derived.Id);

            // The crop was copied to the derived document's own blob, and its text-extraction pipeline was queued.
            await _blobContainer.Received(1).SaveAsync(
                derived.FileOrigin.BlobName, Arg.Any<Stream>(), overrideExisting: true, Arg.Any<CancellationToken>());
            await _backgroundJobManager.Received(1).EnqueueAsync(
                Arg.Is<DocumentTextExtractionJobArgs>(a => a.DocumentId == derived.Id),
                Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
            await _eventBus.Received(1).PublishAsync(
                Arg.Is<DocumentUploadedEto>(e => e.DocumentId == derived.Id));
        });
    }

    [Fact]
    public async Task LowConfidence_Figure_Is_Rejected_And_Crop_Deleted()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, _, cropBlobName) = await ArrangeAsync(sourceId, withType: true);
        StubCropBlob(cropBlobName);
        StubGate(TypeCode, 0.50); // below the type's 0.75 threshold

        await _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == sourceId)).ShouldBeEmpty();
            (await _figureRepository.GetAsync(figureId)).Status.ShouldBe(DocumentFigureStatus.NotADocument);
        });

        await _blobContainer.Received(1).DeleteAsync(cropBlobName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Candidate_Types_Rejects_All_Without_Classifying()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, _, cropBlobName) = await ArrangeAsync(sourceId, withType: false);

        await _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId });

        await WithUnitOfWorkAsync(async () =>
            (await _figureRepository.GetAsync(figureId)).Status.ShouldBe(DocumentFigureStatus.NotADocument));

        await _blobContainer.Received(1).DeleteAsync(cropBlobName, Arg.Any<CancellationToken>());
        // The no-types early path never calls the gate.
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Already_Routed_Figures_Are_Skipped()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, _, _) = await ArrangeAsync(sourceId, withType: true);

        // Pre-mark the only candidate as already spawned, then run: routing must process nothing.
        await WithUnitOfWorkAsync(async () =>
        {
            var figure = await _figureRepository.GetAsync(figureId);
            figure.MarkSpawned(_guidGenerator.Create());
            await _figureRepository.UpdateAsync(figure);
        });

        await _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId });

        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == sourceId)).ShouldBeEmpty());
    }

    [Fact]
    public async Task Figure_Fault_Rethrows_To_Trigger_Job_Retry_And_Leaves_It_Pending()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, _, cropBlobName) = await ArrangeAsync(sourceId, withType: true);
        StubGate(TypeCode, 0.92);          // confident -> proceeds to spawn, which reads the crop
        StubCropBlobThrows(cropBlobName);  // the crop read faults on every run (e.g. transient blob loss)

        // The fault must NOT be swallowed: ExecuteAsync rethrows so ABP reschedules the job. Routing is enqueued
        // only once, so swallowing would strand the figure Pending forever.
        await Should.ThrowAsync<AggregateException>(
            () => _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId }));

        await WithUnitOfWorkAsync(async () =>
        {
            (await _figureRepository.GetAsync(figureId)).Status.ShouldBe(DocumentFigureStatus.Pending);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == sourceId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Blank_Transcription_Figure_Is_Rejected_Without_Calling_The_Gate()
    {
        var sourceId = _guidGenerator.Create();
        var (figureId, _, cropBlobName) = await ArrangeAsync(sourceId, withType: true, transcription: "   ");
        StubCropBlob(cropBlobName);

        await _job.ExecuteAsync(new DocumentFigureRoutingJobArgs { SourceDocumentId = sourceId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == sourceId)).ShouldBeEmpty();
            (await _figureRepository.GetAsync(figureId)).Status.ShouldBe(DocumentFigureStatus.NotADocument);
        });

        await _blobContainer.Received(1).DeleteAsync(cropBlobName, Arg.Any<CancellationToken>());
        // An empty/whitespace transcription is short-circuited to reject; the gate LLM is never called.
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_Derived_Document_For_Same_Source_Figure_Is_Rejected_By_Unique_Index()
    {
        var sourceId = _guidGenerator.Create();
        var figureKey = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];

        await WithUnitOfWorkAsync(async () =>
            await _documentRepository.InsertAsync(NewDerived(sourceId, figureKey), autoSave: true));

        // The filtered unique index on (OriginDocumentId, OriginConstituentKey) is routing's concurrency backstop: a
        // second derived document for the same source figure must be rejected by the database.
        await Should.ThrowAsync<Exception>(() => WithUnitOfWorkAsync(async () =>
            await _documentRepository.InsertAsync(NewDerived(sourceId, figureKey), autoSave: true)));
    }

    private async Task<(Guid FigureId, string ContentHash, string CropBlobName)> ArrangeAsync(
        Guid sourceId, bool withType, string transcription = "INVOICE No. 42 Total 100.00")
    {
        var contentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
        var cropBlobName = $"figures/{sourceId}/{contentHash}";
        var figureId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(new Document(
                sourceId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{sourceId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 1024,
                    originalFileName: "contract.pdf")), autoSave: true);

            if (withType)
            {
                await _typeRepository.InsertAsync(new DocumentType(
                    _guidGenerator.Create(), tenantId: null, typeCode: TypeCode, displayName: "Invoice",
                    confidenceThreshold: Threshold, priority: 0), autoSave: true);
            }

            await _figureRepository.InsertAsync(new DocumentFigure(
                figureId, tenantId: null, sourceDocumentId: sourceId,
                contentHash: contentHash, cropBlobName: cropBlobName, contentType: "image/png",
                transcription: transcription, pageNumber: 2), autoSave: true);
        });

        return (figureId, contentHash, cropBlobName);
    }

    private void StubCropBlob(string cropBlobName)
        => _blobContainer.GetAsync(cropBlobName).Returns(_ => (Stream)new MemoryStream(new byte[] { 1, 2, 3, 4 }));

    private void StubGate(string typeCode, double confidence)
        => _workflow.RunAsync(Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome { TypeCode = typeCode, ConfidenceScore = confidence });

    private void StubCropBlobThrows(string cropBlobName)
        => _blobContainer.GetAsync(cropBlobName).Returns<Stream>(_ => throw new InvalidOperationException("blob unavailable"));

    private Document NewDerived(Guid sourceId, string figureKey)
    {
        var id = _guidGenerator.Create();
        return Document.CreateDerived(
            id, tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{id:N}.png", uploadedByUserName: "test-user", contentType: "image/png",
                contentHash: figureKey, fileSize: 4, originalFileName: "figure.png"),
            originDocumentId: sourceId, originConstituentKey: figureKey);
    }
}

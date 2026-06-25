using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class DocumentAppServiceRerecognizeTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<ExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

/// <summary>
/// <see cref="DocumentAppService.RerecognizeAsync"/> (#263 "rerecognize"): requeue the automatic
/// classification job on existing Markdown, cascading to field re-extraction without rerunning OCR. Key
/// difference from <c>RetryPipelineAsync</c>: <b>already Succeeded classification is allowed</b> as an
/// on-demand rerun, not a failed retry; only Pending/Running is rejected as a concurrency guard.
/// Preconditions: document is not soft-deleted and Markdown has been produced (text extraction succeeded).
/// </summary>
public class DocumentAppService_Rerecognize_Tests
    : ExtractApplicationTestBase<DocumentAppServiceRerecognizeTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_Rerecognize_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task RerecognizeAsync_Requeues_Classification_When_Already_Succeeded()
    {
        // Document is successfully classified (Ready), a state RetryPipelineAsync would reject
        // (NotRetryable). Rerecognize must allow it so AI can rejudge using the latest instructions.
        var doc = await CreateExtractedDocumentAsync();
        var classified = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Classification);
        await _pipelineRunManager.CompleteAsync(doc, classified);
        StubGet(doc);

        await _appService.RerecognizeAsync(doc.Id);

        var newRun = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, ExtractPipelines.Classification);
        newRun.ShouldNotBeNull();
        newRun.Status.ShouldBe(PipelineRunStatus.Pending);
        newRun.AttemptNumber.ShouldBe(2);

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a =>
                a.DocumentId == doc.Id &&
                a.PipelineRunId == newRun.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_Classification_Running()
    {
        var doc = await CreateExtractedDocumentAsync();
        // StartAsync leaves the run in Running state.
        await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(ExtractErrorCodes.Pipeline.RetryInProgress);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_Classification_Pending()
    {
        var doc = await CreateExtractedDocumentAsync();
        // QueueAsync creates a Pending run without Begin, simulating queued and waiting execution.
        await _pipelineRunManager.QueueAsync(doc, ExtractPipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(ExtractErrorCodes.Pipeline.RetryInProgress);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_No_Markdown()
    {
        // Text extraction never produced Markdown, so there is nothing to rejudge classification from.
        var doc = CreateDocument();
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(ExtractErrorCodes.Document.NotTextExtracted);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_Document_Soft_Deleted()
    {
        var doc = await CreateExtractedDocumentAsync();
        doc.IsDeleted = true;
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(ExtractErrorCodes.Document.InRecycleBin);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private void StubGet(Document doc)
    {
        // RerecognizeAsync uses GetAsync(includeDetails:false).
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    // Persist Markdown through the manager's public CompleteParseAsync. SetMarkdown is internal
    // and tests do not call it directly. This simulates the "text extraction succeeded" precondition.
    private async Task<Document> CreateExtractedDocumentAsync()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Parse);
        await _pipelineRunManager.CompleteParseAsync(doc, run, "# Sample\n\nbody", "Sample");
        return doc;
    }

    private static Document CreateDocument(Guid? tenantId = null)
    {
        return new Document(
            Guid.NewGuid(),
            tenantId,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

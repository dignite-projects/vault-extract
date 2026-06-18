using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Documents.Pipelines;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
using Dignite.DocumentAI.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.DocumentAI.Documents;

[DependsOn(typeof(DocumentAIApplicationTestModule))]
public class DocumentAppServiceRetryTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        // #216: Manager depends on IDocumentPipelineRunRepository; use the closure-state fake so
        // QueueAsync/DeriveLifecycle work correctly.
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

/// <summary>
/// <see cref="DocumentAppService.RetryPipelineAsync"/> must follow these rules: only Failed is retryable;
/// Pending and Running are concurrency guards; Succeeded and Skipped are rejected; unknown PipelineCode is
/// rejected. It creates a Pending Run first, then enqueues the matching BackgroundJob.
/// </summary>
public class DocumentAppService_Retry_Tests
    : DocumentAIApplicationTestBase<DocumentAppServiceRetryTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly ICurrentTenant _currentTenant;

    public DocumentAppService_Retry_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task RetryPipelineAsync_Enqueues_TextExtraction_Job_When_Failed()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "OCR engine timeout");
        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction });

        var retryRun = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.TextExtraction);
        retryRun.ShouldNotBeNull();
        retryRun.Status.ShouldBe(PipelineRunStatus.Pending);
        retryRun.AttemptNumber.ShouldBe(2);

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentTextExtractionJobArgs>(a =>
                a.DocumentId == doc.Id &&
                a.PipelineRunId == retryRun.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Enqueues_Classification_Job_When_Failed()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.Classification);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "LLM unavailable");
        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = DocumentAIPipelines.Classification });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Succeeded()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, run);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Pipeline.NotRetryable);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Skipped()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.Classification);
        await _pipelineRunManager.SkipAsync(doc, run, reason: "document too short");
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = DocumentAIPipelines.Classification }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Pipeline.NotRetryable);
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Running()
    {
        var doc = CreateDocument();
        // StartAsync leaves the run in Running state (it calls MarkRunning internally).
        await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = DocumentAIPipelines.Classification }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Pipeline.RetryInProgress);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Pipeline_Never_Ran()
    {
        var doc = CreateDocument();
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Pipeline.NeverRan);
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_For_Unknown_PipelineCode()
    {
        var doc = CreateDocument();
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = "contracts.field-extraction" }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Pipeline.UnknownCode);
        // Unknown PipelineCode must be rejected before reading Document; otherwise business modules would
        // get a bypass for scheduling core jobs.
        await _documentRepository.DidNotReceive().GetAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_EntityNotFound_When_Cross_Tenant()
    {
        // Tenant isolation is applied by the repository's ambient IMultiTenant filter. AppService no
        // longer hand-writes TenantId assertions. When the caller is another tenant, the real repository's
        // GetAsync cannot find the document and throws EntityNotFound. Mock repositories have no filter,
        // so explicitly make GetAsync throw EntityNotFound here to simulate framework behavior and assert
        // AppService propagates it without enqueueing any job.
        var docTenant = Guid.NewGuid();
        var callerTenant = Guid.NewGuid();
        var doc = CreateDocument(tenantId: docTenant);
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .ThrowsAsync(new EntityNotFoundException(typeof(Document), doc.Id));

        using (_currentTenant.Change(callerTenant))
        {
            await Should.ThrowAsync<EntityNotFoundException>(async () =>
                await _appService.RetryPipelineAsync(
                    doc.Id,
                    new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction }));
        }

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_BusinessException_When_Document_Soft_Deleted()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "boom");
        doc.IsDeleted = true;
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction }));

        ex.Code.ShouldBe(DocumentAIErrorCodes.Document.InRecycleBin);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Allows_Second_Retry_After_Second_Failure()
    {
        var doc = CreateDocument();

        // Attempt 1 — fail
        var run1 = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run1, errorMessage: "timeout");

        // Attempt 2 — also fail
        var run2 = await _pipelineRunManager.StartAsync(doc, DocumentAIPipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run2, errorMessage: "timeout again");
        run2.AttemptNumber.ShouldBe(2);

        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = DocumentAIPipelines.TextExtraction });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentTextExtractionJobArgs>(a => a.DocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private void StubGet(Document doc)
    {
        // #216: RetryPipelineAsync now uses GetAsync(includeDetails:false) plus
        // runRepo.FindLatestByDocumentAndCodeAsync.
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);
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

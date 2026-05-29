using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Dignite.Paperbase.Documents.Pipelines.TextExtraction;
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

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceRetryTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// <see cref="DocumentAppService.RetryPipelineAsync"/> 必须按
/// "仅 Failed 可重试 / Pending 与 Running 视为并发护栏 / Succeeded 与 Skipped 拒绝 /
/// 未知 PipelineCode 拒绝"的规则放行，先创建 Pending Run，再把对应 BackgroundJob 入队。
/// </summary>
public class DocumentAppService_Retry_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceRetryTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly ICurrentTenant _currentTenant;

    public DocumentAppService_Retry_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task RetryPipelineAsync_Enqueues_TextExtraction_Job_When_Failed()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "OCR engine timeout");
        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction });

        var retryRun = doc.GetLatestRun(PaperbasePipelines.TextExtraction);
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
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "LLM unavailable");
        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = PaperbasePipelines.Classification });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Succeeded()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, run);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.NotRetryable);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Skipped()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.SkipAsync(doc, run, reason: "document too short");
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = PaperbasePipelines.Classification }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.NotRetryable);
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_When_Latest_Run_Is_Running()
    {
        var doc = CreateDocument();
        // StartAsync leaves the run in Running state (it calls MarkRunning internally).
        await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = PaperbasePipelines.Classification }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.RetryInProgress);
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
                new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.NeverRan);
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

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.UnknownCode);
        // 未知 PipelineCode 必须在读 Document 之前就被拒绝——
        // 否则给业务模块一个旁路调度核心 Job 的口子。
        await _documentRepository.DidNotReceive().GetWithPipelineRunsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPipelineAsync_Throws_EntityNotFound_When_Cross_Tenant()
    {
        // 租户隔离由仓储的 ambient IMultiTenant 过滤器施加（AppService 不再手写 TenantId 断言）：
        // 调用方是别的租户时，真实仓储的 GetAsync 查不到该文档而抛 EntityNotFound。mock 仓储不带过滤器，
        // 此处显式让 GetAsync 抛 EntityNotFound 模拟该框架行为，断言 AppService 如实传播、且不入队任何 Job。
        var docTenant = Guid.NewGuid();
        var callerTenant = Guid.NewGuid();
        var doc = CreateDocument(tenantId: docTenant);
        _documentRepository.GetWithPipelineRunsAsync(doc.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new EntityNotFoundException(typeof(Document), doc.Id));

        using (_currentTenant.Change(callerTenant))
        {
            await Should.ThrowAsync<EntityNotFoundException>(async () =>
                await _appService.RetryPipelineAsync(
                    doc.Id,
                    new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction }));
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
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run, errorMessage: "boom");
        doc.IsDeleted = true;
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RetryPipelineAsync(
                doc.Id,
                new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Document.InRecycleBin);
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
        var run1 = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run1, errorMessage: "timeout");

        // Attempt 2 — also fail
        var run2 = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.FailAsync(doc, run2, errorMessage: "timeout again");
        run2.AttemptNumber.ShouldBe(2);

        StubGet(doc);

        await _appService.RetryPipelineAsync(
            doc.Id,
            new RetryPipelineInput { PipelineCode = PaperbasePipelines.TextExtraction });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentTextExtractionJobArgs>(a => a.DocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private void StubGet(Document doc)
    {
        // RetryPipelineAsync 只需 run 历史，改走 GetWithPipelineRunsAsync。
        _documentRepository.GetWithPipelineRunsAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument(Guid? tenantId = null)
    {
        return new Document(
            Guid.NewGuid(),
            tenantId,
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

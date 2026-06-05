using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceRerecognizeTestModule : AbpModule
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
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

/// <summary>
/// <see cref="DocumentAppService.RerecognizeAsync"/>（#263「重新识别」）——在现有 Markdown 上重排
/// 自动分类 job（级联重抽字段，不重新 OCR）。与 <c>RetryPipelineAsync</c> 的关键差异：
/// <b>对已 Succeeded 的分类也放行</b>（按需重跑，非失败重试）；仅在 Pending/Running 时以并发护栏拒绝。
/// 前置：文档未软删、且已产出 Markdown（文本提取成功）。
/// </summary>
public class DocumentAppService_Rerecognize_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceRerecognizeTestModule>
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
        // 文档已成功分类（Ready）——这是 RetryPipelineAsync 会拒绝（NotRetryable）的状态，
        // 而「重新识别」必须放行：让 AI 依最新说明重判。
        var doc = await CreateExtractedDocumentAsync();
        var classified = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.CompleteAsync(doc, classified);
        StubGet(doc);

        await _appService.RerecognizeAsync(doc.Id);

        var newRun = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
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
        // StartAsync 让 run 停在 Running 状态。
        await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.RetryInProgress);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_Classification_Pending()
    {
        var doc = await CreateExtractedDocumentAsync();
        // QueueAsync 建 Pending run 但不 Begin——模拟已排队等待执行。
        await _pipelineRunManager.QueueAsync(doc, PaperbasePipelines.Classification);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Pipeline.RetryInProgress);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RerecognizeAsync_Throws_When_No_Markdown()
    {
        // 文本提取从未产出 Markdown——无从重判分类。
        var doc = CreateDocument();
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.RerecognizeAsync(doc.Id));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Document.NotTextExtracted);
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

        ex.Code.ShouldBe(PaperbaseErrorCodes.Document.InRecycleBin);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private void StubGet(Document doc)
    {
        // RerecognizeAsync 走 GetAsync(includeDetails:false)。
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    // 经 manager 的公开 CompleteTextExtractionAsync 落 Markdown（SetMarkdown 是 internal，
    // 测试不直接调），模拟「文本提取成功」前置态。
    private async Task<Document> CreateExtractedDocumentAsync()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteTextExtractionAsync(doc, run, "# Sample\n\nbody", "Sample");
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

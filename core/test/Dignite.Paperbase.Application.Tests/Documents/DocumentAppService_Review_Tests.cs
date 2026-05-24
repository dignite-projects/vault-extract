using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceReviewTestModule : AbpModule
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
/// <see cref="DocumentAppService.ApproveReviewAsync"/> + <see cref="DocumentAppService.RejectReviewAsync"/> 行为测试。
/// <para>
/// 核心验收（CLAUDE.md "OCR 置信度门槛" 承诺）：操作员手动确认通过 → 触发 <c>DocumentReadyEto</c>——
/// 具体分两条路径：
/// <list type="bullet">
///   <item>OCR review（classification 未跑）→ schedule classification pipeline，等其完成自然到 Ready</item>
///   <item>classification 已跑且有 type → 即时 RecomputeLifecycle 直接到 Ready</item>
/// </list>
/// </para>
/// </summary>
public class DocumentAppService_Review_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_Review_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task ApproveReviewAsync_When_Not_PendingReview_Is_NoOp()
    {
        var doc = CreateDocument();
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);  // 初始即非 PendingReview
        StubGet(doc);

        await _appService.ApproveReviewAsync(doc.Id);

        // 幂等：不动状态，不 schedule，不 update
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ApproveReviewAsync_OcrReview_Schedules_Classification_When_Not_Run()
    {
        // OCR review 场景：text-extraction Run Succeeded（confidence 不达标），classification 从未跑
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);
        doc.MarkPendingOcrReview("OCR confidence 0.40 below threshold 0.80");
        StubGet(doc);

        await _appService.ApproveReviewAsync(doc.Id);

        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.Reviewed);
        doc.ClassificationReason.ShouldBeNull();

        // classification job 被 enqueue（后续完成时由 DeriveLifecycle 自然推进到 Ready）
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ApproveReviewAsync_ClassificationReview_With_Type_Recomputes_Lifecycle_To_Ready()
    {
        // 分类已完成 + 有 DocumentTypeCode 的 PendingReview 场景（罕见——通常分类置信度低应走 ReclassifyAsync）。
        // 此路径下不重新 schedule classification，而是 RecomputeLifecycle 即时推进到 Ready。
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);

        var classRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        // 模拟"分类成功 + 有 type"但人工置 PendingReview 的场景
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, ["host.general", 0.88]);
        await _pipelineRunManager.CompleteAsync(doc, classRun);
        doc.MarkPendingOcrReview("ops manual hold");  // 手工置 PendingReview
        StubGet(doc);

        await _appService.ApproveReviewAsync(doc.Id);

        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.Reviewed);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);

        // 不应再 schedule classification（已经跑过）
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ApproveReviewAsync_ClassificationReview_Without_Type_Returns_Current_Pending_State()
    {
        // 边界 case：DocumentTypeCode 为 null（分类置信度低 / 无合适类型）。
        // ApproveReview 不应向客户端抛错，也不能把文档移出 PendingReview 后又无法 Ready。
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);

        var classRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, reason: "ambiguous");
        StubGet(doc);

        doc.DocumentTypeCode.ShouldBeNull();
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        var dto = await _appService.ApproveReviewAsync(doc.Id);

        dto.DocumentTypeCode.ShouldBeNull();
        dto.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
        dto.ClassificationReason.ShouldBe("ambiguous");
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
        doc.ClassificationReason.ShouldBe("ambiguous");
        doc.LifecycleStatus.ShouldNotBe(DocumentLifecycleStatus.Ready);  // type 空不能 Ready

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task RejectReviewAsync_Transitions_Lifecycle_To_Failed()
    {
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);
        doc.MarkPendingOcrReview("OCR garbage");
        StubGet(doc);

        await _appService.RejectReviewAsync(doc.Id, new RejectReviewInput { Reason = "scan unusable" });

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);
        doc.ClassificationReason.ShouldBe("scan unusable");
    }

    [Fact]
    public void PendingReview_Filter_Excludes_Failed_Rejections_By_Default()
    {
        var activePending = CreateDocument();
        activePending.MarkPendingOcrReview("needs review");

        var rejected = CreateDocument();
        rejected.MarkPendingOcrReview("OCR garbage");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { activePending, rejected }.AsQueryable(),
                new GetDocumentListInput { ReviewStatus = DocumentReviewStatus.PendingReview })
            .ToList();

        result.ShouldContain(activePending);
        result.ShouldNotContain(rejected);
    }

    [Fact]
    public void PendingReview_Filter_Allows_Failed_Rejections_When_Lifecycle_Is_Explicit()
    {
        var rejected = CreateDocument();
        rejected.MarkPendingOcrReview("OCR garbage");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { rejected }.AsQueryable(),
                new GetDocumentListInput
                {
                    ReviewStatus = DocumentReviewStatus.PendingReview,
                    LifecycleStatus = DocumentLifecycleStatus.Failed
                })
            .ToList();

        result.ShouldContain(rejected);
    }

    private void StubGet(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            originalFileBlobName: $"blobs/{Guid.NewGuid():N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    private IQueryable<Document> ApplyFilterForTest(IQueryable<Document> query, GetDocumentListInput input)
    {
        var service = new DocumentAppService(
            Substitute.For<IDocumentRepository>(),
            Substitute.For<IDocumentTypeRepository>(),
            Substitute.For<IFieldDefinitionRepository>(),
            Substitute.For<ICabinetRepository>(),
            Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>(),
            new DocumentPipelineRunManager(),
            new DocumentPipelineJobScheduler(
                Substitute.For<IDocumentRepository>(),
                new DocumentPipelineRunManager(),
                Substitute.For<IBackgroundJobManager>()),
            Substitute.For<IDistributedEventBus>());

        var method = typeof(DocumentAppService).GetMethod(
            "ApplyFilter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (IQueryable<Document>)method.Invoke(service, [query, input])!;
    }
}

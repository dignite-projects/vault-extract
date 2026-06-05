using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentClassificationJobTestModule : AbpModule
{
    // 已知 Id 让测试断言 doc.DocumentTypeId == ContractTypeId（#207：分类结果是内部 Id）。
    public static readonly Guid ContractTypeId = Guid.NewGuid();

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        // #216：Manager + 后台作业 BeginRun/CompleteRun/FailRun 都走 IDocumentPipelineRunRepository。
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());

        // 字段架构 v2：候选集来自 IDocumentTypeRepository（DB），按 Document.TenantId 精确匹配单层
        var contractType = new DocumentType(
            ContractTypeId,
            tenantId: null,
            typeCode: "contract.general",
            displayName: "合同",
            confidenceThreshold: 0.75,
            priority: 0);

        var typeRepo = Substitute.For<IDocumentTypeRepository>();
        typeRepo.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType> { contractType });
        typeRepo.FindByTypeCodeAsync("contract.general", Arg.Any<CancellationToken>())
            .Returns(contractType);
        typeRepo.FindByTypeCodeAsync(Arg.Is<string>(s => s != "contract.general"), Arg.Any<CancellationToken>())
            .Returns((DocumentType?)null);
        context.Services.AddSingleton(typeRepo);

        var workflow = Substitute.ForPartsOf<DocumentClassificationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new PaperbaseAIBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);

        context.Services.Configure<PaperbaseAIBehaviorOptions>(_ => { });
    }
}

/// <summary>
/// DocumentClassificationBackgroundJob 行为测试：验证分类结果如何驱动
/// PipelineRun 状态流转和 DocumentClassifiedEto 发布。
/// IChatClient 和 DocumentClassificationWorkflow 均使用 NSubstitute 替代，无真实 LLM 调用。
/// </summary>
public class DocumentClassificationBackgroundJob_Tests
    : PaperbaseApplicationTestBase<DocumentClassificationJobTestModule>
{
    private readonly DocumentClassificationBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;

    public DocumentClassificationBackgroundJob_Tests()
    {
        _job = GetRequiredService<DocumentClassificationBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _workflow = GetRequiredService<DocumentClassificationWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task HighConfidence_Completes_Pipeline_And_Publishes_Event()
    {
        var doc = CreateDocument("業務委託契約書の内容です。");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.92,
                Reason = "Contains contract keywords"
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);
        doc.ClassificationConfidence.ShouldBe(0.92);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentClassifiedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.ClassificationConfidence == 0.92),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task LowConfidence_Marks_PendingReview_No_Event()
    {
        var doc = CreateDocument("Some document text.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.50,
                Reason = "Low confidence"
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task NullTypeCode_Marks_PendingReview()
    {
        var doc = CreateDocument("Unrecognized document.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = null,
                ConfidenceScore = 0.0
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
        doc.DocumentTypeId.ShouldBeNull();
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
    }

    [Fact]
    public async Task UnregisteredTypeCode_Marks_PendingReview_Without_Polluting_Document()
    {
        // LLM 幻觉：返回一个不在 DocumentType 注册表中的 TypeCode
        var doc = CreateDocument("Some document text.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "invoice.general",
                ConfidenceScore = 0.95,
                Reason = "LLM hallucinated an unknown type"
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Transient_AiProviderFailure_FailsRun_And_Rethrows_For_AbpRetry()
    {
        var doc = CreateDocument("業務委託契約書。甲：A社。乙：B社。");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<DocumentClassificationOutcome>(_ => throw new TimeoutException("AI service timeout"));

        await Should.ThrowAsync<TimeoutException>(
            async () => await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id }));

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Failed);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task JsonException_Routes_To_PendingReview()
    {
        var doc = CreateDocument("業務委託契約書。甲：A社。乙：B社。");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<DocumentClassificationOutcome>(_ => throw new JsonException("schema mismatch: missing required field"));

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Wrapped_JsonException_Also_Routes_To_PendingReview()
    {
        var doc = CreateDocument("業務委託契約書。");
        SetupDocumentRepository(doc);

        var inner = new JsonException("invalid token");
        var outer = new InvalidOperationException("LLM response could not be parsed", inner);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<DocumentClassificationOutcome>(_ => throw outer);

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        doc.DocumentTypeId.ShouldBeNull();
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    private void SetupDocumentRepository(Document doc)
    {
        // #216：分类作业三处加载从 GetWithPipelineRunsAsync 改为 GetAsync(includeDetails:false)——
        // PipelineRun 独立聚合根后通过 runRepo 单独查。BeginRun 仍走 GetAsync。
        _documentRepository
            .GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);

        // #267：CompleteRun 改走 FindWithFieldValuesAsync(includeFieldValues:true)——低置信度路径需带字段值
        // 加载才能清空类型绑定字段。两处返回同一 doc 实例，断言（DocumentTypeId/ReviewStatus）不受影响。
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument(string extractedText)
    {
        var doc = new Document(
            Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        typeof(Document)
            .GetProperty(nameof(Document.Markdown))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(doc, [extractedText]);

        return doc;
    }
}

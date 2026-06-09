using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Review;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceReviewTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // #207：DTO 组装走 ResolveReferenceMapsAsync → GetListAsync(谓词) 批量解析 Id→code/name。
        // 默认 stub 返回空 list（避免 NSubstitute 对 Task<List<T>> 返回 null 触发 NRE）；具体用例可按需覆盖。
        var documentTypeRepository = Substitute.For<IDocumentTypeRepository>();
        documentTypeRepository
            .GetListAsync(Arg.Any<Expression<Func<DocumentType, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType>());
        context.Services.AddSingleton(documentTypeRepository);

        var fieldDefinitionRepository = Substitute.For<IFieldDefinitionRepository>();
        fieldDefinitionRepository
            .GetListAsync(Arg.Any<Expression<Func<FieldDefinition, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());
        context.Services.AddSingleton(fieldDefinitionRepository);
    }
}

/// <summary>
/// <see cref="DocumentAppService.RejectReviewAsync"/> 行为（#237：落 ReviewStatus=Rejected，可恢复）+ 审核列表过滤测试。
/// <para>
/// #196 砍掉 OCR 置信度门槛后，进 PendingReview 的唯一来源是分类低置信度 / 无合适类型
/// （<see cref="DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync"/>）；
/// 操作员的审核动作只剩 Reclassify（指派类型）或 Reject，不再有"裸放行"(approve)。
/// </para>
/// </summary>
public class DocumentAppService_Review_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_Review_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task RejectReviewAsync_Transitions_Lifecycle_To_Failed()
    {
        var doc = await CreatePendingReviewDocumentAsync("ambiguous");
        StubGet(doc);

        await _appService.RejectReviewAsync(doc.Id, new RejectReviewInput { Reason = "scan unusable" });

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Rejected);
        doc.RejectionReason.ShouldBe("scan unusable");
    }

    [Fact]
    public async Task PendingReview_Filter_Excludes_Rejected_Documents_By_Default()
    {
        var activePending = await CreatePendingReviewDocumentAsync("needs review");

        // 被拒绝的文档落 ReviewDisposition=Rejected（#284）；它仍保留 UC 原因，但审核队列
        // （HasReviewReasons）显式排除已拒绝文档，故不出现在队列里。
        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { activePending, rejected }.AsQueryable(),
                new GetDocumentListInput { HasReviewReasons = true })
            .ToList();

        result.ShouldContain(activePending);
        result.ShouldNotContain(rejected);
    }

    [Fact]
    public async Task RejectedDocuments_Are_Queryable_By_Rejected_ReviewStatus()
    {
        // #237：拒绝可恢复 + 留痕——reject 把 ReviewStatus 落到 Rejected（权威信号），
        // 操作员 / 下游据此显式查询被拒文档；LifecycleStatus 仍是 Failed 的"宏观不可用"外观。
        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { rejected }.AsQueryable(),
                new GetDocumentListInput { ReviewDisposition = DocumentReviewDisposition.Rejected })
            .ToList();

        result.ShouldContain(rejected);
    }

    [Fact]
    public async Task GetAsync_With_Missing_Required_Field_Exposes_Review_Reason_Detail()
    {
        // #284：详情 DTO 出口审核明细——必填缺失原因(non-blocking) + 缺失字段 DisplayName，服务端算/客户端纯渲染。
        var typeId = Guid.NewGuid();
        var doc = CreateDocument();
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [typeId, 0.99]);
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);
        StubGet(doc);

        // 该类型有一个必填字段 "amount"(DisplayName "金额")，文档未抽到 → 缺失。
        var amountDef = new FieldDefinition(
            Guid.NewGuid(), tenantId: null, documentTypeId: typeId,
            name: "amount", displayName: "金额", prompt: null,
            dataType: FieldDataType.Number, displayOrder: 0, isRequired: true);
        GetRequiredService<IFieldDefinitionRepository>()
            .GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { amountDef });

        var dto = await _appService.GetAsync(doc.Id);

        dto.RequiresReview.ShouldBeTrue();
        dto.ReviewReasons.ShouldBe(DocumentReviewReasons.MissingRequiredFields);
        dto.ReviewReasonDetails.ShouldNotBeNull();
        var detail = dto.ReviewReasonDetails.ShouldHaveSingleItem();
        detail.Reason.ShouldBe(DocumentReviewReasons.MissingRequiredFields);
        detail.IsBlocking.ShouldBeFalse();   // MRF 是 non-blocking
        detail.MissingFieldNames.ShouldNotBeNull();
        detail.MissingFieldNames.ShouldContain("金额");
    }

    [Fact]
    public async Task GetAsync_For_Rejected_Document_Does_Not_Report_RequiresReview()
    {
        // #284 review-fix：拒绝可恢复故保留客观原因(UC)，但出口 RequiresReview 必为 false、明细置空——
        // 统一判据 RequiresAttention(reasons, disposition) 排除 Rejected，避免"已拒绝 + 待审"自相矛盾、计数虚高。
        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");
        // 拒绝刻意不动 ReviewReasons：UC 原因仍在，仅 disposition 转 Rejected。
        rejected.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        rejected.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Rejected);
        StubGet(rejected);

        var dto = await _appService.GetAsync(rejected.Id);

        dto.RequiresReview.ShouldBeFalse();
        dto.ReviewReasonDetails.ShouldBeNull();
    }

    [Fact]
    public void ConfirmClassification_Clears_Stale_RejectionReason()
    {
        // #284 review-fix：操作员对已拒绝文档 Reclassify 指派类型 → ConfirmClassification 把处置转回 Confirmed，
        // 陈旧 RejectionReason 必须清空（仅 Rejected 时该有值）。ConfirmClassification 为 internal，反射调用
        //（与 GetAsync_With_Missing_Required_Field 的 ApplyAutomaticClassificationResult 同手法）。
        var doc = CreateDocument();
        doc.RejectReview("scan unusable");
        doc.RejectionReason.ShouldBe("scan unusable");

        typeof(Document)
            .GetMethod("ConfirmClassification",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [Guid.NewGuid()]);

        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);
        doc.RejectionReason.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_With_Confirmed_Type_But_Missing_Required_Field_Still_Requires_Review()
    {
        // #284 review-fix：操作员已确认类型(Confirmed)但字段重抽仍缺必填 → MRF 置位、disposition=Confirmed。
        // 统一判据 disposition!=Rejected 对 Confirmed 也成立 → RequiresReview 仍 true（与 NotReviewed 不同的处置态分支，
        // 钉死"误写成 disposition==NotReviewed"的回归）。
        var typeId = Guid.NewGuid();
        var doc = CreateDocument();
        typeof(Document)
            .GetMethod("ConfirmClassification",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [typeId]);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);
        StubGet(doc);

        var amountDef = new FieldDefinition(
            Guid.NewGuid(), tenantId: null, documentTypeId: typeId,
            name: "amount", displayName: "金额", prompt: null,
            dataType: FieldDataType.Number, displayOrder: 0, isRequired: true);
        GetRequiredService<IFieldDefinitionRepository>()
            .GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { amountDef });

        var dto = await _appService.GetAsync(doc.Id);

        dto.RequiresReview.ShouldBeTrue();
        dto.ReviewReasonDetails.ShouldNotBeNull();
        dto.ReviewReasonDetails.ShouldContain(d => d.Reason == DocumentReviewReasons.MissingRequiredFields);
    }

    [Fact]
    public async Task GetAsync_With_MRF_Flag_But_No_Missing_Names_Skips_Detail_But_Keeps_RequiresReview()
    {
        // #284 fix #4：MRF 位置位但缺失字段名解析为空（in-flight schema 漂移：曾缺的必填被软删 / 翻非必填）→
        // 跳过空壳明细、明细返回 null，但 RequiresReview 仍 true（MRF flag 由抽取阶段权威维护，不因明细暂空翻转）。
        var typeId = Guid.NewGuid();
        var doc = CreateDocument();
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [typeId, 0.99]);
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);
        StubGet(doc);

        // 该类型当前无必填字段（全非必填 / 已软删）→ 缺失字段名集为空。
        GetRequiredService<IFieldDefinitionRepository>()
            .GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var dto = await _appService.GetAsync(doc.Id);

        dto.RequiresReview.ShouldBeTrue();
        dto.ReviewReasonDetails.ShouldBeNull();
    }

    [Fact]
    public async Task RejectReviewAsync_With_Empty_Reason_Throws_Validation()
    {
        // #284：拒绝理由必填（RejectReviewInput.Reason [Required]）——空理由必须被 AppService 校验拦截。
        var doc = await CreatePendingReviewDocumentAsync("needs review");
        StubGet(doc);

        await Should.ThrowAsync<AbpValidationException>(() =>
            _appService.RejectReviewAsync(doc.Id, new RejectReviewInput { Reason = "" }));
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
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    /// <summary>
    /// 造一个进入 PendingReview 的文档：text-extraction 成功 → classification 低置信度
    /// （#196 后这是 PendingReview 的唯一产生路径）。DocumentTypeCode 保持 null。
    /// </summary>
    private async Task<Document> CreatePendingReviewDocumentAsync(string reason)
    {
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);
        var classRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, reason: reason);
        return doc;
    }

    private IQueryable<Document> ApplyFilterForTest(IQueryable<Document> query, GetDocumentListInput input)
    {
        // #216：DocumentPipelineRunManager 依赖 IDocumentPipelineRunRepository（AppService 自 follow-up #6 起
        // 不再直接依赖——retry 状态机判定下沉到 manager.EnsureRetryableAsync）；本测试只反射调 ApplyFilter
        // （纯 LINQ 函数，不触发 manager / repo 实际调用），传 Substitute 即可。
        var runRepoSubstitute = Substitute.For<Pipelines.IDocumentPipelineRunRepository>();
        var service = new DocumentAppService(
            Substitute.For<IDocumentRepository>(),
            Substitute.For<IDocumentTypeRepository>(),
            Substitute.For<IFieldDefinitionRepository>(),
            Substitute.For<ICabinetRepository>(),
            Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>(),
            new DocumentPipelineRunManager(runRepoSubstitute),
            new DocumentPipelineJobScheduler(
                Substitute.For<IDocumentRepository>(),
                new DocumentPipelineRunManager(runRepoSubstitute),
                Substitute.For<IBackgroundJobManager>()),
            Substitute.For<IDistributedEventBus>(),
            new ReviewStateEvaluator());

        var method = typeof(DocumentAppService).GetMethod(
            "ApplyFilter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // #207：ApplyFilter 现签名 (query, input, documentTypeId?)；这些用例只过滤 ReviewStatus，类型传 null。
        return (IQueryable<Document>)method.Invoke(service, [query, input, null])!;
    }
}

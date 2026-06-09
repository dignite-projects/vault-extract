using System;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.Documents.Pipelines;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents;

public class DocumentPipelineRunManagerTests : PaperbaseDomainTestBase<PaperbaseDomainTestModule>
{
    private readonly DocumentPipelineRunManager _manager;
    private readonly IDocumentPipelineRunRepository _runRepo;

    public DocumentPipelineRunManagerTests()
    {
        _manager = GetRequiredService<DocumentPipelineRunManager>();
        _runRepo = GetRequiredService<IDocumentPipelineRunRepository>();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────

    private static Document CreateDocument()
    {
        var fileOrigin = new FileOrigin(
            blobName: "blobs/test.pdf",
            uploadedByUserName: "test-user",
            contentType: "application/pdf",
            contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            fileSize: 1024,
            originalFileName: "test.pdf");

        return new Document(
            id: Guid.NewGuid(),
            tenantId: null,
            fileOrigin: fileOrigin);
    }

    private static DocumentType CreateContractType() => new(
        id: Guid.NewGuid(),
        tenantId: null,
        typeCode: "contract.general",
        displayName: "Contract",
        confidenceThreshold: 0.7,
        priority: 0);

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 1: all key pipelines succeed → Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_Creates_Pending_Run_Before_BackgroundJob_Execution()
    {
        var doc = CreateDocument();

        var run = await _manager.QueueAsync(doc, PaperbasePipelines.TextExtraction);

        run.Status.ShouldBe(PipelineRunStatus.Pending);
        run.AttemptNumber.ShouldBe(1);
        (await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.TextExtraction))
            .ShouldBe(run);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
    }

    [Fact]
    public async Task All_KeyPipelines_Succeed_Transitions_To_Ready()
    {
        var doc = CreateDocument();
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Uploaded);

        // TextExtraction
        var textRun = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        await _manager.CompleteAsync(doc, textRun);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing); // Classification not done

        // Classification
        var classRun = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, classRun, CreateContractType(), 0.92);

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
    }

    // #284：必填缺失(MissingRequiredFields)是 non-blocking——有它也不挡 Ready
    // （必填缺失只进操作员队列，绝不阻断下游 DocumentReadyEto）。这是本次重构的核心正确性。
    [Fact]
    public async Task NonBlocking_MissingRequiredFields_Does_Not_Block_Ready()
    {
        var doc = CreateDocument();
        // 预置 MissingRequiredFields（模拟字段抽取已判定必填缺失）。
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);

        var textRun = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _manager.CompleteAsync(doc, textRun);
        var classRun = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, classRun, CreateContractType(), 0.92);

        // 关键流水线成功 + 类型确认 + 无 blocking 原因 → Ready；MRF（non-blocking）仍保留。
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.MissingRequiredFields);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 2: key pipeline (TextExtraction) fails → Failed
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextExtraction_Fail_Transitions_To_Failed()
    {
        var doc = CreateDocument();

        var run = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _manager.FailAsync(doc, run, errorMessage: "OCR engine error");

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 3: retry increments AttemptNumber; latest run state takes effect
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_Increments_AttemptNumber_And_Latest_Run_State_Wins()
    {
        var doc = CreateDocument();

        // Attempt 1 — fail
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        run1.AttemptNumber.ShouldBe(1);
        await _manager.FailAsync(doc, run1, errorMessage: "timeout");

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);

        // Attempt 2 — succeed (retry)
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        run2.AttemptNumber.ShouldBe(2);

        // While running, LifecycleStatus goes back to Processing (latest is Running)
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        await _manager.CompleteAsync(doc, run2);

        // Latest run for TextExtraction is now Succeeded; Classification still missing → Processing
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        // Verify latest run for TextExtraction is attempt 2 (via runRepo, #216 拆分后从聚合根读取)
        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.TextExtraction);
        latestRun.ShouldNotBeNull();
        latestRun.AttemptNumber.ShouldBe(2);
        latestRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 6: CompleteClassificationWithLowConfidenceAsync completes Run and
    //             sets ReviewReasons=UnresolvedClassification (low-confidence signal is on
    //             Document.ReviewReasons, not on the Run)
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteClassificationWithLowConfidence_Sets_ReviewStatus_And_Completes_Run()
    {
        var doc = CreateDocument();
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        var run = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run, "AI confidence too low");

        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // 不变量「无已确认类型 ⟹ 无类型绑定字段值」(#267)：低置信度收回类型时一并清空旧字段值，
    // 否则出口读模型会出现「无类型却带字段」。重新识别一个已抽过字段的文档落到低置信度时暴露。
    [Fact]
    public async Task CompleteClassificationWithLowConfidence_Clears_ExtractedFieldValues()
    {
        var doc = CreateDocument();
        // 模拟已分类 + 已抽字段的既有态。
        doc.SetFields(new[]
        {
            new DocumentFieldValue(Guid.NewGuid(), FieldDataType.Text, JsonSerializer.SerializeToElement("Acme")),
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        var run = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run, "AI confidence too low");

        doc.DocumentTypeId.ShouldBeNull();
        doc.ExtractedFieldValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task LowConfidence_Does_Not_Transition_To_Ready()
    {
        var doc = CreateDocument();

        var textRun = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _manager.CompleteAsync(doc, textRun);

        var classRun = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, "AI confidence too low");

        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.DocumentTypeId.ShouldBeNull();
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 7: CompleteManualClassificationAsync writes TypeCode, marks
    //             ReviewDisposition=Confirmed, completes Run (manual-override signal
    //             is on Document.ReviewDisposition)
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteManualClassification_Sets_TypeCode_Marks_Reviewed_And_Completes_Run()
    {
        var doc = CreateDocument();

        // 先模拟低置信度进入待审核
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        // 人工确认
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteManualClassificationAsync(doc, run2, contractType);

        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(1.0);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);

        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, PaperbasePipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 8: auto-classification success after PendingReview resets ReviewStatus
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoClassification_Success_After_PendingReview_Resets_ReviewStatus_To_None()
    {
        var doc = CreateDocument();

        // 第一次低置信度 → 置 UnresolvedClassification 待审原因（#284：不再持久化分类理由）
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1, "AI confidence too low");
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        // 重试自动分类成功 → 高置信度路径必须清除 UnresolvedClassification 待审原因
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteClassificationAsync(doc, run2, contractType, 0.95);

        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(0.95);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None); // 高置信度路径清除待审原因
    }

    [Fact]
    public async Task AutoClassification_After_Reject_Clears_Stale_RejectionReason()
    {
        // #284 review-fix：拒绝可恢复——被拒文档（RejectionReason 有值）经高置信度自动重分类
        // （ApplyAutomaticClassificationResult）后，陈旧 RejectionReason 必须清空、处置回 NotReviewed
        //（RejectionReason 仅在 ReviewDisposition=Rejected 时该有值）。
        var doc = CreateDocument();

        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1, "AI confidence too low");
        doc.RejectReview("scan unusable");
        doc.RejectionReason.ShouldBe("scan unusable");
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Rejected);

        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, run2, CreateContractType(), 0.95);

        doc.RejectionReason.ShouldBeNull();
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 9: 先成功分类再 LowConfidence —— 历史 TypeCode 与 Confidence 被清空
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LowConfidence_After_Successful_Classification_Clears_Stale_Fields()
    {
        var doc = CreateDocument();

        // 第一次：高置信度分类成功
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteClassificationAsync(doc, run1, contractType, 0.92);

        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(0.92);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        // 第二次：重跑分类落入 LowConfidence
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run2, "AI confidence too low");

        // 历史 TypeId/Confidence 必须清空，避免外部读到「类型已确定 + 待审核」自相矛盾
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 10：typeCode 合法性校验已移到 AppService（先 load DocumentType 不存在则 throw），
    //              manager 不再做 DB 查询。原 Scenario 10 两个测试相关性已转移到 Application.Tests。
    // ────────────────────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 11: TextExtraction completion persists Markdown + Title atomically;
    //              Title accepts null and is write-once for non-null values.
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteTextExtraction_Persists_Markdown_And_Title()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);

        await _manager.CompleteTextExtractionAsync(
            doc, run, markdown: "# Hello\n\nbody", title: "Hello");

        doc.Markdown.ShouldBe("# Hello\n\nbody");
        doc.Title.ShouldBe("Hello");
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task CompleteTextExtraction_Persists_Language_And_Metadata()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);

        var metadata = new DocumentTextExtractionMetadata(
            "PaddleOCR",
            new NativePayloadManifest("extraction-native/" + doc.Id, "application/json", 42, "abc123", "PaddleOCR/PP-StructureV3"));

        await _manager.CompleteTextExtractionAsync(
            doc, run, markdown: "# Scan\n\nbody", title: "Scan",
            language: "ja", extractionMetadata: metadata);

        doc.Language.ShouldBe("ja");
        doc.ExtractionMetadata.ShouldNotBeNull();
        doc.ExtractionMetadata!.ProviderName.ShouldBe("PaddleOCR");
        doc.ExtractionMetadata.NativePayloadManifest.ShouldNotBeNull();
        doc.ExtractionMetadata.NativePayloadManifest!.BlobName.ShouldBe("extraction-native/" + doc.Id);
        doc.ExtractionMetadata.NativePayloadManifest.Sha256.ShouldBe("abc123");
    }

    [Fact]
    public async Task CompleteTextExtraction_Allows_Null_Title_For_Backfill_Path()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);

        await _manager.CompleteTextExtractionAsync(
            doc, run, markdown: "irrelevant", title: null);

        doc.Title.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteTextExtraction_Title_Is_WriteOnce()
    {
        var doc = CreateDocument();

        // 1st extraction succeeds with a title
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _manager.CompleteTextExtractionAsync(doc, run1, "# Original", "Original");

        // A second completion attempt must fail — Markdown invariant guards write-once.
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _manager.CompleteTextExtractionAsync(doc, run2, "# Other", "Other");
        });

        // Title remains the originally persisted value.
        doc.Title.ShouldBe("Original");
    }
}

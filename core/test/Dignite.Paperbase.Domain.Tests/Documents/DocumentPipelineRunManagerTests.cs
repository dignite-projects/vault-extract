using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents;

public class DocumentPipelineRunManagerTests : PaperbaseDomainTestBase<PaperbaseDomainTestModule>
{
    private readonly DocumentPipelineRunManager _manager;

    public DocumentPipelineRunManagerTests()
    {
        _manager = GetRequiredService<DocumentPipelineRunManager>();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────

    private static Document CreateDocument()
    {
        var fileOrigin = new FileOrigin(
            uploadedByUserName: "test-user",
            contentType: "application/pdf",
            contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            fileSize: 1024,
            originalFileName: "test.pdf");

        return new Document(
            id: Guid.NewGuid(),
            tenantId: null,
            originalFileBlobName: "blobs/test.pdf",
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
        doc.GetLatestRun(PaperbasePipelines.TextExtraction).ShouldBe(run);
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

        // Verify GetLatestRun returns attempt 2
        var latestRun = doc.GetLatestRun(PaperbasePipelines.TextExtraction);
        latestRun.ShouldNotBeNull();
        latestRun.AttemptNumber.ShouldBe(2);
        latestRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 6: CompleteClassificationWithLowConfidenceAsync completes Run and
    //             sets ReviewStatus to PendingReview (low-confidence signal is on
    //             Document.ReviewStatus, not on the Run)
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteClassificationWithLowConfidence_Sets_ReviewStatus_And_Completes_Run()
    {
        var doc = CreateDocument();
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);

        var run = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run, "AI confidence too low");

        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        var latestRun = doc.GetLatestRun(PaperbasePipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task LowConfidence_Does_Not_Transition_To_Ready()
    {
        var doc = CreateDocument();

        var textRun = await _manager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _manager.CompleteAsync(doc, textRun);

        var classRun = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, "AI confidence too low");

        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
        doc.DocumentTypeId.ShouldBeNull();
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 7: CompleteManualClassificationAsync writes TypeCode, marks Reviewed,
    //             completes Run (manual-override signal is on Document.ReviewStatus)
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteManualClassification_Sets_TypeCode_Marks_Reviewed_And_Completes_Run()
    {
        var doc = CreateDocument();

        // 先模拟低置信度进入待审核
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);

        // 人工确认
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteManualClassificationAsync(doc, run2, contractType);

        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(1.0);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.Reviewed);

        var latestRun = doc.GetLatestRun(PaperbasePipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 8: auto-classification success after PendingReview resets ReviewStatus
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoClassification_Success_After_PendingReview_Resets_ReviewStatus_To_None()
    {
        var doc = CreateDocument();

        // 第一次低置信度 → PendingReview（写入 ClassificationReason）
        var run1 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1, "AI confidence too low");
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
        doc.ClassificationReason.ShouldBe("AI confidence too low");

        // 重试自动分类成功 → 高置信度路径必须清空 ClassificationReason
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteClassificationAsync(doc, run2, contractType, 0.95);

        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);
        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(0.95);
        doc.ClassificationReason.ShouldBeNull(); // 高置信度路径固定清空
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
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.None);

        // 第二次：重跑分类落入 LowConfidence
        var run2 = await _manager.StartAsync(doc, PaperbasePipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run2, "AI confidence too low");

        // 历史 TypeId/Confidence 必须清空，避免外部读到「类型已确定 + 待审核」自相矛盾
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewStatus.ShouldBe(DocumentReviewStatus.PendingReview);
        doc.ClassificationReason.ShouldBe("AI confidence too low");
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

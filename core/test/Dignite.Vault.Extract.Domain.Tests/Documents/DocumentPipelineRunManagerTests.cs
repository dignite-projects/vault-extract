using System;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

public class DocumentPipelineRunManagerTests : VaultExtractDomainTestBase<VaultExtractDomainTestModule>
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

        var run = await _manager.QueueAsync(doc, VaultExtractPipelines.Parse);

        run.Status.ShouldBe(PipelineRunStatus.Pending);
        run.AttemptNumber.ShouldBe(1);
        (await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Parse))
            .ShouldBe(run);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
    }

    [Fact]
    public async Task All_KeyPipelines_Succeed_Transitions_To_Ready()
    {
        var doc = CreateDocument();
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Uploaded);

        // Parse
        var textRun = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        await _manager.CompleteAsync(doc, textRun);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing); // Classification not done

        // Classification
        var classRun = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, classRun, CreateContractType(), 0.92);
        // #411: field-extraction is now a key pipeline, so Ready is withheld until it succeeds too.
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        // Field extraction
        var fieldRun = await _manager.StartAsync(doc, VaultExtractPipelines.FieldExtraction);
        await _manager.CompleteAsync(doc, fieldRun);

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
    }

    // #284: MissingRequiredFields is non-blocking and does not prevent Ready. Missing required fields
    // only enter the operator queue and never block downstream DocumentReadyEto. This is the core
    // correctness point of the refactor.
    [Fact]
    public async Task NonBlocking_MissingRequiredFields_Does_Not_Block_Ready()
    {
        var doc = CreateDocument();
        // Pre-set MissingRequiredFields, simulating field extraction already detecting missing required
        // fields.
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);

        var textRun = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _manager.CompleteAsync(doc, textRun);
        var classRun = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, classRun, CreateContractType(), 0.92);
        // #411: field-extraction must also succeed before Ready (it is a key pipeline now).
        var fieldRun = await _manager.StartAsync(doc, VaultExtractPipelines.FieldExtraction);
        await _manager.CompleteAsync(doc, fieldRun);

        // Critical pipelines succeeded, type is confirmed, and no blocking reason exists, so Ready. MRF
        // is non-blocking and remains present.
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.MissingRequiredFields);
    }

    // #411: DuplicateSuspected is a blocking reason. Even with all three key pipelines succeeded, a suspected
    // duplicate withholds Ready so downstream never consumes it; the document waits in the operator review queue.
    [Fact]
    public async Task DuplicateSuspected_Blocks_Ready_Even_When_All_KeyPipelines_Succeed()
    {
        var doc = CreateDocument();

        var textRun = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _manager.CompleteAsync(doc, textRun);
        var classRun = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, classRun, CreateContractType(), 0.92);

        // Field extraction succeeds but detects a duplicate fingerprint -> sets the blocking reason.
        var fieldRun = await _manager.StartAsync(doc, VaultExtractPipelines.FieldExtraction);
        doc.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
        await _manager.CompleteAsync(doc, fieldRun);

        // #510: all three key pipelines succeeded but DuplicateSuspected (blocking) withholds Ready, so the
        // availability appearance is PendingReview (waiting on the operator), not Processing (still running).
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.PendingReview);

        // Operator decides it is not a duplicate -> clearing the reason + re-deriving releases it to Ready.
        doc.AllowDuplicate();
        doc.DuplicateAllowed.ShouldBeTrue();
        await _manager.ReDeriveLifecycleAsync(doc);

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
    }

    // #346: a container is a correct outcome with no type. Completing classification as a container marks the
    // document, leaves the type null, sets NO blocking review reason, and — with both key pipelines succeeded —
    // derives straight to Ready (Design A), so the container is never parked in the operator review queue.
    [Fact]
    public async Task Container_Classification_Derives_To_Ready_Without_Review_Reason()
    {
        var doc = CreateDocument();

        var textRun = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _manager.CompleteAsync(doc, textRun);

        var classRun = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsContainerAsync(doc, classRun);

        doc.IsContainer.ShouldBeTrue();
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        // Distinct from the low-confidence path: NO UnresolvedClassification, so it does not enter the review queue.
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);

        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // #346: marking a container clears any field values already extracted (a container holds no single type's
    // fields), mirroring the #267 "no confirmed type implies no field values" invariant.
    [Fact]
    public async Task Container_Classification_Clears_ExtractedFieldValues()
    {
        var doc = CreateDocument();
        doc.SetFields(new[]
        {
            new DocumentFieldValue(Guid.NewGuid(), FieldDataType.Text, JsonSerializer.SerializeToElement("Acme")),
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsContainerAsync(doc, run);

        doc.IsContainer.ShouldBeTrue();
        doc.DocumentTypeId.ShouldBeNull();
        doc.ExtractedFieldValues.ShouldBeEmpty();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 2: key pipeline (Parse) fails → Failed
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_Fail_Transitions_To_Failed()
    {
        var doc = CreateDocument();

        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
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
        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        run1.AttemptNumber.ShouldBe(1);
        await _manager.FailAsync(doc, run1, errorMessage: "timeout");

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);

        // Attempt 2 — succeed (retry)
        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        run2.AttemptNumber.ShouldBe(2);

        // While running, LifecycleStatus goes back to Processing (latest is Running)
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        await _manager.CompleteAsync(doc, run2);

        // Latest run for Parse is now Succeeded; Classification still missing → Processing
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

        // Verify latest run for Parse is attempt 2 via runRepo, reading through the aggregate
        // root after the #216 split.
        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Parse);
        latestRun.ShouldNotBeNull();
        latestRun.AttemptNumber.ShouldBe(2);
        latestRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // Regression: a run reused IN PLACE across Fail → retry → Succeed must not keep the prior failure message.
    // Unlike the retry scenario above (which creates a new attempt via StartAsync), ABP's automatic in-job retry
    // re-runs the same background job with the same args, so the scheduler-enqueued job's fixed PipelineRunId makes
    // BeginOrStartAsync re-begin the SAME run row (AttemptNumber unchanged). StatusMessage was only ever written on
    // failure and never cleared, so before the fix a reused run could end up Succeeded while still carrying the old
    // AI/model-error text — which the detail page kept rendering under a green "Succeeded" badge.
    [Fact]
    public async Task Reused_Run_On_Retry_Success_Clears_Stale_StatusMessage()
    {
        var doc = CreateDocument();

        // The attempt fails with a diagnostic message (e.g. the LLM/model provider error).
        var run = await _manager.StartAsync(doc, VaultExtractPipelines.FieldExtraction);
        await _manager.FailAsync(doc, run, errorMessage: "AI provider returned malformed output");
        run.Status.ShouldBe(PipelineRunStatus.Failed);
        run.StatusMessage.ShouldBe("AI provider returned malformed output");

        // ABP retries the same job in place: BeginOrStartAsync re-begins THIS run (fixed PipelineRunId), not a new
        // attempt, so AttemptNumber stays 1 and re-entering Running must wipe the stale message.
        await _manager.BeginAsync(doc, run);
        run.AttemptNumber.ShouldBe(1);
        run.StatusMessage.ShouldBeNull();

        // The retried attempt now succeeds (e.g. after the host switched the model) — no leftover failure text.
        await _manager.CompleteAsync(doc, run);
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
        run.StatusMessage.ShouldBeNull();
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

        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run, "AI confidence too low");

        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // Invariant "no confirmed type implies no type-bound field values" (#267): when low confidence
    // withdraws the type, old field values must be cleared too. Otherwise the output read model would
    // show fields without a type. This is exposed when rerecognizing a document that already has
    // extracted fields and falls into low confidence.
    [Fact]
    public async Task CompleteClassificationWithLowConfidence_Clears_ExtractedFieldValues()
    {
        var doc = CreateDocument();
        // Simulate an existing state with classification and extracted fields.
        doc.SetFields(new[]
        {
            new DocumentFieldValue(Guid.NewGuid(), FieldDataType.Text, JsonSerializer.SerializeToElement("Acme")),
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run, "AI confidence too low");

        doc.DocumentTypeId.ShouldBeNull();
        doc.ExtractedFieldValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task LowConfidence_Does_Not_Transition_To_Ready()
    {
        var doc = CreateDocument();

        var textRun = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _manager.CompleteAsync(doc, textRun);

        var classRun = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, "AI confidence too low");

        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.DocumentTypeId.ShouldBeNull();
        // #510 (Semantics B): a blocking reason alone makes it PendingReview, not Processing — even though
        // field-extraction never ran (no confirmed type), the machine has gone as far as it can and is waiting
        // on the operator to confirm a type. Still not Ready, so DocumentReadyEto is not fired.
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.PendingReview);
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

        // First simulate low confidence entering pending review.
        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        // Manual confirmation.
        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteManualClassificationAsync(doc, run2, contractType);

        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(1.0);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);

        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Classification);
        latestRun!.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 8: auto-classification success after PendingReview resets ReviewStatus
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoClassification_Success_After_PendingReview_Resets_ReviewStatus_To_None()
    {
        var doc = CreateDocument();

        // First low confidence: set UnresolvedClassification review reason (#284: classification reason
        // is no longer persisted).
        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1, "AI confidence too low");
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        // Automatic classification succeeds on retry; the high-confidence path must clear the
        // UnresolvedClassification review reason.
        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteClassificationAsync(doc, run2, contractType, 0.95);

        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(0.95);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None); // High-confidence path clears the review reason.
    }

    [Fact]
    public async Task AutoClassification_After_Reject_Clears_Stale_RejectionReason()
    {
        // #284 review-fix: rejection is recoverable. After a rejected document with RejectionReason is
        // automatically reclassified with high confidence (ApplyAutomaticClassificationResult), the stale
        // RejectionReason must be cleared and disposition must return to NotReviewed. RejectionReason is
        // present only when ReviewDisposition=Rejected.
        var doc = CreateDocument();

        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run1, "AI confidence too low");
        doc.RejectReview("scan unusable");
        doc.RejectionReason.ShouldBe("scan unusable");
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Rejected);

        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationAsync(doc, run2, CreateContractType(), 0.95);

        doc.RejectionReason.ShouldBeNull();
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 9: successful classification followed by LowConfidence clears historical TypeCode and Confidence
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LowConfidence_After_Successful_Classification_Clears_Stale_Fields()
    {
        var doc = CreateDocument();

        // First: high-confidence classification succeeds.
        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        var contractType = CreateContractType();
        await _manager.CompleteClassificationAsync(doc, run1, contractType, 0.92);

        doc.DocumentTypeId.ShouldBe(contractType.Id);
        doc.ClassificationConfidence.ShouldBe(0.92);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        // Second: rerunning classification falls into LowConfidence.
        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _manager.CompleteClassificationWithLowConfidenceAsync(doc, run2, "AI confidence too low");

        // Historical TypeId/Confidence must be cleared to avoid external readers seeing the contradictory
        // state "type is determined + pending review".
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 10: typeCode validation has moved to AppService, which first loads DocumentType and throws
    //              when missing. The manager no longer performs DB queries. The two original Scenario 10
    //              test concerns have moved to Application.Tests.
    // ────────────────────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 11: Parse completion persists Markdown + Title atomically;
    //              Title accepts null and is write-once for non-null values.
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteParse_Persists_Markdown_And_Title()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);

        await _manager.CompleteParseAsync(
            doc, run, markdown: "# Hello\n\nbody", title: "Hello");

        doc.Markdown.ShouldBe("# Hello\n\nbody");
        doc.Title.ShouldBe("Hello");
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task CompleteParse_Persists_Language_And_Metadata()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);

        var metadata = new DocumentParseMetadata(
            "PaddleOCR",
            new NativePayloadManifest("extraction-native/" + doc.Id, "application/json", 42, "abc123", "PaddleOCR/PP-StructureV3"));

        await _manager.CompleteParseAsync(
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
    public async Task CompleteParse_Allows_Null_Title_For_Backfill_Path()
    {
        var doc = CreateDocument();
        var run = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);

        await _manager.CompleteParseAsync(
            doc, run, markdown: "irrelevant", title: null);

        doc.Title.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteParse_Title_Is_WriteOnce()
    {
        var doc = CreateDocument();

        // 1st extraction succeeds with a title
        var run1 = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _manager.CompleteParseAsync(doc, run1, "# Original", "Original");

        // A second completion attempt must fail — Markdown invariant guards write-once.
        var run2 = await _manager.StartAsync(doc, VaultExtractPipelines.Parse);
        await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _manager.CompleteParseAsync(doc, run2, "# Other", "Other");
        });

        // Title remains the originally persisted value.
        doc.Title.ShouldBe("Original");
    }
}

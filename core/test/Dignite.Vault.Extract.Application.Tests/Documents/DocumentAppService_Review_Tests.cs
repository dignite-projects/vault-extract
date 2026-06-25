using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Review;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class DocumentAppServiceReviewTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<ExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // #207: DTO assembly goes through ResolveReferenceMapsAsync -> GetListAsync(predicate) to batch
        // resolve Id to code/name. Default stub returns an empty list to avoid NSubstitute returning null
        // for Task<List<T>> and causing NRE; individual cases may override as needed.
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
/// Behavior tests for <see cref="DocumentAppService.RejectReviewAsync"/> (#237: sets
/// ReviewStatus=Rejected and is recoverable) plus review-list filtering tests.
/// <para>
/// After #196 removed the OCR confidence gate, the only source of PendingReview is classification low
/// confidence / no suitable type (<see cref="DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync"/>).
/// Operator review actions are now only Reclassify (assign a type) or Reject; there is no bare approve.
/// </para>
/// </summary>
public class DocumentAppService_Review_Tests
    : ExtractApplicationTestBase<DocumentAppServiceReviewTestModule>
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

        // Rejected document gets ReviewDisposition=Rejected (#284). It still keeps the UC reason, but the
        // review queue (HasReviewReasons) explicitly excludes rejected documents, so it does not appear.
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
        // #237: rejection is recoverable and leaves trace. reject sets ReviewStatus to Rejected as the
        // authoritative signal, and operators / downstream consumers explicitly query rejected documents
        // from it. LifecycleStatus remains Failed as the broad "unavailable" appearance.
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
        // #284: detail DTO outputs review details: non-blocking missing-required-fields reason plus
        // missing field DisplayName. Server computes, client only renders.
        var typeId = Guid.NewGuid();
        var doc = CreateDocument();
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [typeId, 0.99]);
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);
        StubGet(doc);

        // This type has one required field "amount", and the document did not extract it, so it is missing.
        var amountDef = new FieldDefinition(
            Guid.NewGuid(), tenantId: null, documentTypeId: typeId,
            name: "amount", displayName: "Amount", prompt: null,
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
        detail.IsBlocking.ShouldBeFalse();   // MRF is non-blocking
        detail.MissingFieldNames.ShouldNotBeNull();
        detail.MissingFieldNames.ShouldContain("Amount");
    }

    [Fact]
    public async Task GetAsync_For_Rejected_Document_Does_Not_Report_RequiresReview()
    {
        // #284 review-fix: rejection is recoverable, so keep the objective reason (UC), but output
        // RequiresReview must be false and details cleared. Unified rule RequiresAttention(reasons,
        // disposition) excludes Rejected, avoiding contradictory "rejected + pending review" state and
        // inflated counts.
        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");
        // Rejection deliberately does not touch ReviewReasons: the UC reason remains, and only disposition
        // changes to Rejected.
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
        // #284 review-fix: when an operator Reclassifies a rejected document and assigns a type,
        // ConfirmClassification changes disposition back to Confirmed, and stale RejectionReason must be
        // cleared because it is valid only for Rejected. ConfirmClassification is internal, so invoke it
        // by reflection, same as ApplyAutomaticClassificationResult in GetAsync_With_Missing_Required_Field.
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
        // #284 review-fix: operator already confirmed type (Confirmed), but field re-extraction still
        // misses required fields, so MRF is set and disposition=Confirmed.
        // Unified rule disposition!=Rejected also applies to Confirmed, so RequiresReview remains true.
        // This locks a disposition branch distinct from NotReviewed and prevents regression to
        // disposition==NotReviewed.
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
            name: "amount", displayName: "Amount", prompt: null,
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
        // #284 fix #4: MRF bit is set but missing field name resolution is empty due to in-flight schema
        // drift, such as a previously missing required field being soft-deleted or changed to non-required.
        // Skip empty-shell details and return null details, but RequiresReview remains true. The MRF flag
        // is maintained authoritatively by extraction and does not flip because details are temporarily
        // empty.
        var typeId = Guid.NewGuid();
        var doc = CreateDocument();
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [typeId, 0.99]);
        doc.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);
        StubGet(doc);

        // This type currently has no required fields, all non-required or soft-deleted, so the missing
        // field name set is empty.
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
        // #284: rejection reason is required (RejectReviewInput.Reason [Required]), so empty reason must
        // be blocked by AppService validation.
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
    /// Creates a document entering PendingReview: text-extraction succeeds, then classification has low
    /// confidence. After #196 this is the only path producing PendingReview. DocumentTypeCode remains
    /// null.
    /// </summary>
    private async Task<Document> CreatePendingReviewDocumentAsync(string reason)
    {
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Parse);
        await _pipelineRunManager.CompleteAsync(doc, textRun);
        var classRun = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Classification);
        await _pipelineRunManager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, reason: reason);
        return doc;
    }

    private IQueryable<Document> ApplyFilterForTest(IQueryable<Document> query, GetDocumentListInput input)
    {
        // #216: DocumentPipelineRunManager depends on IDocumentPipelineRunRepository. AppService no longer
        // depends on it directly since follow-up #6, because retry state-machine judgment moved down to
        // manager.EnsureRetryableAsync. This test only invokes ApplyFilter by reflection, a pure LINQ
        // function that does not actually call manager / repo, so a Substitute is enough.
        var runRepoSubstitute = Substitute.For<Pipelines.IDocumentPipelineRunRepository>();
        var service = new DocumentAppService(
            Substitute.For<IDocumentRepository>(),
            Substitute.For<IDocumentTypeRepository>(),
            Substitute.For<IFieldDefinitionRepository>(),
            Substitute.For<ICabinetRepository>(),
            Substitute.For<IBlobContainer<ExtractDocumentContainer>>(),
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
        // #207: ApplyFilter signature is now (query, input, documentTypeId?). These cases filter only
        // ReviewStatus, so pass null for type.
        return (IQueryable<Document>)method.Invoke(service, [query, input, null])!;
    }
}

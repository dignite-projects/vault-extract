using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Pipelines;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
using Dignite.DocumentAI.Documents.Pipelines.Segmentation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.DocumentAI.Documents;

[DependsOn(typeof(DocumentAIApplicationTestModule))]
public class DocumentClassificationJobTestModule : AbpModule
{
    // Known Id lets tests assert doc.DocumentTypeId == ContractTypeId (#207: classification result is the internal Id).
    public static readonly Guid ContractTypeId = Guid.NewGuid();

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        // #371: classification now reads the marked-Markdown blob (GetAllBytesOrNullAsync) to see [Image OCR] figure
        // spans for the embedded-document signal. A substitute container returns null from GetOrNullAsync, so the job
        // falls back to Document.Markdown — preserving these tests' clean-Markdown classification behavior.
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        // #216: Manager + background-job BeginRun / CompleteRun / FailRun all use IDocumentPipelineRunRepository.
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());

        // Field schema v2: the candidate set comes from IDocumentTypeRepository (DB), matched exactly to
        // one layer by Document.TenantId.
        var contractType = new DocumentType(
            ContractTypeId,
            tenantId: null,
            typeCode: "contract.general",
            displayName: "Contract",
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
            Options.Create(new DocumentAIBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);

        context.Services.Configure<DocumentAIBehaviorOptions>(_ => { });
    }
}

/// <summary>
/// DocumentClassificationBackgroundJob behavior tests: verifies how classification results drive
/// PipelineRun status transitions and DocumentClassifiedEto publication.
/// IChatClient and DocumentClassificationWorkflow are both replaced with NSubstitute, with no real LLM calls.
/// </summary>
public class DocumentClassificationBackgroundJob_Tests
    : DocumentAIApplicationTestBase<DocumentClassificationJobTestModule>
{
    private readonly DocumentClassificationBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentClassificationWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _markedBlobContainer;

    public DocumentClassificationBackgroundJob_Tests()
    {
        _job = GetRequiredService<DocumentClassificationBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _workflow = GetRequiredService<DocumentClassificationWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _markedBlobContainer = GetRequiredService<IBlobContainer<DocumentAIDocumentContainer>>();
    }

    [Fact]
    public async Task HighConfidence_Completes_Pipeline_And_Publishes_Event()
    {
        var doc = CreateDocument("This is the content of a service agreement.");
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

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);
        doc.ClassificationConfidence.ShouldBe(0.92);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentClassifiedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.ClassificationConfidence == 0.92),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Container_Marks_Document_And_Does_Not_Publish_ClassifiedEto()
    {
        var doc = CreateDocument("Invoice 1 ... Invoice 2 ... Invoice 3 ... (a bundle of independent invoices)");
        SetupDocumentRepository(doc);

        // #346: the classifier reports a container. Even with an incidental high-confidence type guess, the
        // container branch dominates: it must not be classified to that type and must not cascade extraction.
        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                IsContainer = true,
                TypeCode = "contract.general",
                ConfidenceScore = 0.95,
                Reason = "Several independent documents bundled together"
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.IsContainer.ShouldBeTrue();
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        // A container is a correct outcome, not low confidence: it must not enter the operator review queue.
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        // The crux of Design A: never publishing DocumentClassifiedEto is what stops FieldExtractionEventHandler
        // from cascading field extraction onto the container.
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());

        // #371: a detected container enqueues the unified sub-document segmentation job (same UoW as the completion).
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentSegmentationJobArgs>(a => a.SourceDocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Derived_SubDocument_Flagged_Container_Is_Classified_Normally_Not_Recursed()
    {
        // #346 recursion guard: a derived sub-document must never be re-detected as a container, or the split would
        // recurse. Even if the LLM flags it a container, it is classified normally (here to a real type) and the
        // segmentation job is NOT enqueued.
        var doc = CreateDerivedDocument("A single invoice that the LLM mistook for a bundle.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                IsContainer = true,
                TypeCode = "contract.general",
                ConfidenceScore = 0.95
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        doc.IsContainer.ShouldBeFalse();
        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentSegmentationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        // It classified to a real type, so the normal field-extraction cascade event still fires.
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentClassifiedEto>(e => e.DocumentId == doc.Id), Arg.Any<bool>());
    }

    [Fact]
    public async Task EmbeddedDocument_NonContainer_Routes_And_Still_Publishes_ClassifiedEto()
    {
        // #371 D6(a): a non-container parent the classifier flags as embedding a standalone document still extracts its
        // own fields (DocumentClassifiedEto fires) AND enqueues the unified sub-document pass to route the embedded
        // document. The two are not mutually exclusive — the parent is a real typed document that happens to embed one.
        var doc = CreateDocument("A service agreement that has a scanned invoice photo embedded in it.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.92,
                ContainsEmbeddedDocument = true,
                Reason = "A contract that embeds a standalone invoice"
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        // The parent is still classified and its own field-extraction cascade fires.
        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentClassifiedEto>(e => e.DocumentId == doc.Id), Arg.Any<bool>());

        // AND the unified sub-document pass is enqueued to route the embedded document.
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentSegmentationJobArgs>(a => a.SourceDocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task NoEmbeddedDocument_SmallDocument_Does_Not_Route()
    {
        // #371 D6(b): the common case — a small, figure-free document the classifier does NOT flag as embedding a
        // standalone document must NOT pay for the heavy segmentation pass. Guards a regression that drops the
        // ContainsEmbeddedDocument disjunct or inverts the markedLength comparison.
        var doc = CreateDocument("A short single service agreement with no embedded documents.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.92,
                ContainsEmbeddedDocument = false
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentSegmentationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task FigureBearing_OverWindow_Document_Routes_Even_Without_Embedded_Signal()
    {
        // #371 D6(b) fallback arm: a figure-bearing document whose MARKED Markdown exceeds the classification
        // truncation window (MaxTextLengthPerExtraction = 8000) may carry an embedded figure the classifier never saw
        // (the tail was truncated out of the prompt). It must route even when ContainsEmbeddedDocument is false, so a
        // beyond-window embedded document is not silently missed.
        var doc = CreateDocument("Leading body text used as the clean fallback Markdown.");
        SetupDocumentRepository(doc);

        // Marked-Markdown blob: a real salted [Image OCR] span (so ImageOcrMarkup.Contains == true) padded beyond the
        // window. GetAllBytesOrNullAsync is an extension over the interface GetOrNullAsync(Stream), so stub the latter.
        var markedMarkdown = ImageOcrMarkup.Wrap(new string('x', 9000), pageNumber: 2);
        var markedBytes = Encoding.UTF8.GetBytes(markedMarkdown);
        var markedBlobName = DocumentConsts.MarkedMarkdownBlobPrefix + doc.Id;
        _markedBlobContainer
            .GetOrNullAsync(markedBlobName, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(markedBytes));

        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.92,
                ContainsEmbeddedDocument = false
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentSegmentationJobArgs>(a => a.SourceDocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Automatic_Reclassify_Of_Segmented_Container_To_Concrete_Type_Clears_IsSegmented()
    {
        // #371/#377 post-merge fix: the AUTOMATIC high-confidence path (ApplyAutomaticClassificationResult, reachable
        // via RerecognizeAsync) must clear IsSegmented on a container->concrete transition, exactly like the operator
        // path (ConfirmClassification). Otherwise the stale resume marker silently gates off the now-concrete
        // document's own embedded-document routing (DocumentSegmentationJob's !AlreadySegmented gate skips the split),
        // so the embedded figure is never routed. This test fails on the pre-fix code (IsSegmented stays true).
        var doc = CreateDocument("A document once detected as a container, now re-recognized as a single contract.");
        typeof(Document)
            .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, null);
        doc.MarkSegmented(); // simulate the prior container segmentation having completed
        doc.IsContainer.ShouldBeTrue();
        doc.IsSegmented.ShouldBeTrue();
        SetupDocumentRepository(doc);

        // Automatic high-confidence classification to a concrete type that also embeds a standalone document.
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<DocumentType>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentClassificationOutcome
            {
                TypeCode = "contract.general",
                ConfidenceScore = 0.92,
                ContainsEmbeddedDocument = true
            });

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        doc.IsContainer.ShouldBeFalse();
        doc.DocumentTypeId.ShouldBe(DocumentClassificationJobTestModule.ContractTypeId);
        // The fix: the stale resume marker is cleared, so the re-enqueued segmentation pass will actually run instead
        // of being skipped by !AlreadySegmented — the now-concrete document's embedded figure can be routed.
        doc.IsSegmented.ShouldBeFalse();
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentSegmentationJobArgs>(a => a.SourceDocumentId == doc.Id),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
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

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

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

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
        doc.DocumentTypeId.ShouldBeNull();
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);
    }

    [Fact]
    public async Task UnregisteredTypeCode_Marks_PendingReview_Without_Polluting_Document()
    {
        // LLM hallucination: returns a TypeCode that is not in the DocumentType registry.
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

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Transient_AiProviderFailure_FailsRun_And_Rethrows_For_AbpRetry()
    {
        var doc = CreateDocument("Service agreement. Party A: Company A. Party B: Company B.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<DocumentClassificationOutcome>(_ => throw new TimeoutException("AI service timeout"));

        await Should.ThrowAsync<TimeoutException>(
            async () => await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id }));

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Failed);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task JsonException_Routes_To_PendingReview()
    {
        var doc = CreateDocument("Service agreement. Party A: Company A. Party B: Company B.");
        SetupDocumentRepository(doc);

        _workflow
            .RunAsync(
                Arg.Any<IReadOnlyList<DocumentType>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<DocumentClassificationOutcome>(_ => throw new JsonException("schema mismatch: missing required field"));

        await _job.ExecuteAsync(new DocumentClassificationJobArgs { DocumentId = doc.Id });

        var run = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, DocumentAIPipelines.Classification);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Wrapped_JsonException_Also_Routes_To_PendingReview()
    {
        var doc = CreateDocument("Service agreement.");
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
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.UnresolvedClassification);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    private void SetupDocumentRepository(Document doc)
    {
        // #216: classification job loading in three places changed from GetWithPipelineRunsAsync to
        // GetAsync(includeDetails:false). After PipelineRun became an independent aggregate root, runRepo
        // is queried separately. BeginRun still uses GetAsync.
        _documentRepository
            .GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);

        // #267: CompleteRun now uses FindWithFieldValuesAsync(includeFieldValues:true); low-confidence paths
        // need field values loaded to clear type-bound fields. Both return the same doc instance, so assertions
        // for DocumentTypeId / ReviewStatus are unaffected.
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

    private static Document CreateDerivedDocument(string extractedText)
    {
        // A born-digital sub-document: OriginDocumentId is set, which is what the recursion guard keys on.
        var doc = Document.CreateDerived(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{Guid.NewGuid():N}.md",
                uploadedByUserName: "test-user",
                contentType: "text/markdown",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                fileSize: 256,
                originalFileName: "segment-abc.md"),
            originDocumentId: Guid.NewGuid(),
            originConstituentKey: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64]);

        typeof(Document)
            .GetProperty(nameof(Document.Markdown))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(doc, [extractedText]);

        return doc;
    }
}

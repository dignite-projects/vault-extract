using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentParseDeclaredTypeJobTestModule : AbpModule
{
    public static readonly Guid InvoiceTypeId = Guid.NewGuid();

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        // #216: Manager + background-job BeginRun / CompleteRun / FailRun all use IDocumentPipelineRunRepository.
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());

        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        context.Services.AddKeyedSingleton(
            VaultExtractConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
        // Only exercised for a derived document (OriginDocumentId set), which these tests do not construct;
        // registered purely so DocumentParseBackgroundJob's constructor resolves.
        context.Services.AddSingleton(Substitute.For<IRepository<DocumentSegment, Guid>>());

        var invoiceType = new DocumentType(InvoiceTypeId, tenantId: null, typeCode: "invoice.general", displayName: "Invoice");
        var typeRepo = Substitute.For<IDocumentTypeRepository>();
        typeRepo.FindAsync(InvoiceTypeId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(invoiceType);
        context.Services.AddSingleton(typeRepo);

        context.Services.Configure<VaultExtractBehaviorOptions>(_ => { });
    }
}

/// <summary>
/// #623 decision 2: the Parse-cascade branch for an upload-declared document type. A document that already
/// carries <see cref="Document.DocumentTypeId"/> + <see cref="DocumentReviewDisposition.Confirmed"/> before
/// Parse ever ran (set by <see cref="Document.DeclareDocumentType"/> at upload) short-circuits the automatic
/// LLM classification job and instead completes the Classification stage as a manual classification, in the
/// same UoW that completes Parse.
/// </summary>
public class DocumentParseBackgroundJob_DeclaredType_Tests
    : VaultExtractApplicationTestBase<DocumentParseDeclaredTypeJobTestModule>
{
    private readonly DocumentParseBackgroundJob _job;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;

    public DocumentParseBackgroundJob_DeclaredType_Tests()
    {
        _job = GetRequiredService<DocumentParseBackgroundJob>();
        _pipelineJobScheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
    }

    [Fact]
    public async Task DeclaredType_ShortCircuits_Automatic_Classification_And_Cascades_FieldExtraction()
    {
        var doc = CreateDocument();
        // Simulates UploadAsync's DeclareDocumentType call, made before Parse ever runs.
        _pipelineRunManager.DeclareDocumentType(doc, DocumentParseDeclaredTypeJobTestModule.InvoiceTypeId);
        SetupDocumentRepository(doc);

        var run = await _pipelineJobScheduler.QueueAsync(doc, VaultExtractPipelines.Parse);
        StubExtraction("# Invoice\n\nTotal 100.00");

        await _job.ExecuteAsync(new DocumentParseJobArgs { DocumentId = doc.Id, PipelineRunId = run.Id });

        var classificationRun = await _runRepository.FindLatestByDocumentAndCodeAsync(
            doc.Id, VaultExtractPipelines.Classification);
        classificationRun.ShouldNotBeNull();
        classificationRun.Status.ShouldBe(PipelineRunStatus.Succeeded);

        // #527 §8: the cascade field-extraction run is created transactionally with classification completion.
        var feRun = await _runRepository.FindLatestByDocumentAndCodeAsync(
            doc.Id, VaultExtractPipelines.FieldExtraction);
        feRun.ShouldNotBeNull();
        feRun.Status.ShouldBe(PipelineRunStatus.Pending);
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentFieldExtractionJobArgs>(a => a.DocumentId == doc.Id && a.PipelineRunId == feRun.Id),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentClassifiedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "invoice.general" &&
                e.ClassificationConfidence == 1.0),
            Arg.Any<bool>());

        // The crux of #623: no LLM classification job is ever enqueued for a declared-type document.
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());

        doc.DocumentTypeId.ShouldBe(DocumentParseDeclaredTypeJobTestModule.InvoiceTypeId);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);
        doc.ClassificationConfidence.ShouldBe(1.0);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
    }

    [Fact]
    public async Task DeclaredType_Deleted_Before_Parse_Falls_Back_To_Automatic_Classification()
    {
        var doc = CreateDocument();
        var missingTypeId = Guid.NewGuid(); // no FindAsync stub -> the type repo mock returns null for this id
        _pipelineRunManager.DeclareDocumentType(doc, missingTypeId);
        SetupDocumentRepository(doc);

        var run = await _pipelineJobScheduler.QueueAsync(doc, VaultExtractPipelines.Parse);
        StubExtraction("# Something");

        await _job.ExecuteAsync(new DocumentParseJobArgs { DocumentId = doc.Id, PipelineRunId = run.Id });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());

        // Code review on #623 (2026-09-05): the stale declaration must be retracted, not left dangling, so the
        // persisted row never shows Confirmed against a DocumentTypeId that no longer resolves to anything while
        // the newly-queued automatic classification job is still pending.
        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0d);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
    }

    private void SetupDocumentRepository(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.GetAsync(doc.Id, true, Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
    }

    private void StubExtraction(string markdown)
    {
        _blobContainer.GetAsync(Arg.Any<string>())
            .Returns(Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _textExtractor.ExtractAsync(
                Arg.Any<Stream>(),
                Arg.Any<TextExtractionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new TextExtractionResult
            {
                Markdown = markdown,
                DetectedLanguage = "en",
                ProviderName = "ElBruno.MarkItDotNet"
            });
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

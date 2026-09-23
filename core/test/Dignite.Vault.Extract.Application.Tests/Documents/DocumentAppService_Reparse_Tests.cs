using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentAppServiceReparseTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

/// <summary>
/// <see cref="DocumentAppService.ReparseAsync"/> (#660): queues a text-extraction run marked as a re-parse, which
/// the parse job completes by replacing the parse outputs, withdrawing the old sub-documents and queueing
/// classification. Unlike <c>RetryPipelineAsync</c>, an already Succeeded text extraction is the normal starting
/// point; every stage the re-parse restarts must be idle. The entry point refuses what re-parse cannot mean: a
/// document in the recycle bin, one without Markdown (a failed first parse is retried instead) and a sub-document
/// (re-parse its parent). The mode is decided when the run is queued, including by a retry of a failed re-parse.
/// </summary>
public class DocumentAppService_Reparse_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceReparseTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;

    public DocumentAppService_Reparse_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        // The original file is in storage unless a test says otherwise.
        _blobContainer.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task ReparseAsync_Queues_A_Text_Extraction_Run_Marked_As_A_Reparse()
    {
        // A processed document: text extraction and classification both succeeded, the state RetryPipelineAsync
        // would refuse as NotRetryable.
        var doc = await CreateExtractedDocumentAsync();
        var classified = await _pipelineRunManager.StartAsync(doc, VaultExtractPipelines.Classification);
        await _pipelineRunManager.CompleteAsync(doc, classified);
        StubGet(doc);

        await _appService.ReparseAsync(doc.Id);

        var newRun = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.Parse);
        newRun.ShouldNotBeNull();
        newRun.Status.ShouldBe(PipelineRunStatus.Pending);
        newRun.AttemptNumber.ShouldBe(2);

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentParseJobArgs>(a =>
                a.DocumentId == doc.Id &&
                a.PipelineRunId == newRun.Id &&
                a.IsReparse),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData(VaultExtractPipelines.Parse)]
    [InlineData(VaultExtractPipelines.Classification)]
    [InlineData(VaultExtractPipelines.FieldExtraction)]
    public async Task ReparseAsync_Throws_While_A_Stage_It_Restarts_Is_Running(string pipelineCode)
    {
        var doc = await CreateExtractedDocumentAsync();
        // StartAsync leaves the run in Running state.
        await _pipelineRunManager.StartAsync(doc, pipelineCode);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(doc.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Pipeline.RetryInProgress);
        await AssertNothingQueuedAsync();
    }

    [Theory]
    [InlineData(VaultExtractPipelines.Classification)]
    [InlineData(VaultExtractPipelines.FieldExtraction)]
    public async Task ReparseAsync_Throws_While_A_Stage_It_Restarts_Is_Pending(string pipelineCode)
    {
        var doc = await CreateExtractedDocumentAsync();
        // QueueAsync creates a Pending run without Begin, simulating a queued job.
        await _pipelineRunManager.QueueAsync(doc, pipelineCode);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(doc.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Pipeline.RetryInProgress);
        await AssertNothingQueuedAsync();
    }

    [Fact]
    public async Task ReparseAsync_Throws_When_No_Markdown()
    {
        // The first parse never produced Markdown: that is a failed parse to retry, not one to replace.
        var doc = CreateDocument();
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(doc.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.NotTextExtracted);
        await AssertNothingQueuedAsync();
    }

    [Fact]
    public async Task ReparseAsync_Throws_When_Document_Soft_Deleted()
    {
        var doc = await CreateExtractedDocumentAsync();
        doc.IsDeleted = true;
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(doc.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.InRecycleBin);
        await AssertNothingQueuedAsync();
    }

    [Fact]
    public async Task ReparseAsync_Refuses_A_Sub_Document()
    {
        // A sub-document has no file of its own: its Markdown is a slice of its parent's. Re-parsing the parent is
        // what re-splits it.
        var subDocument = Document.CreateDerived(
            Guid.NewGuid(), tenantId: null, fileOrigin: null,
            originDocumentId: Guid.NewGuid(), originConstituentKey: new string('a', 64), creatorId: null);
        var run = await _pipelineRunManager.StartAsync(subDocument, VaultExtractPipelines.Parse);
        await _pipelineRunManager.CompleteParseAsync(subDocument, run, "# Slice\n\nbody", "Slice");
        StubGet(subDocument);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(subDocument.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.ReparseSubDocument);
        await AssertNothingQueuedAsync();
    }

    [Fact]
    public async Task ReparseAsync_Refuses_When_The_Original_File_Is_Not_In_Storage()
    {
        // Accepted, the background run would fail and leave a document whose text is intact marked Failed, with
        // every retry failing the same way — found on the dev database, where a shared DB outlives a local blob store.
        var doc = await CreateExtractedDocumentAsync();
        _blobContainer.ExistsAsync(doc.FileOrigin!.BlobName, Arg.Any<CancellationToken>()).Returns(false);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ReparseAsync(doc.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.ReparseSourceFileMissing);
        await AssertNothingQueuedAsync();
    }

    [Fact]
    public async Task Retrying_A_Failed_Reparse_Queues_Another_Reparse()
    {
        // The document has Markdown and its latest text-extraction run failed: only a re-parse can have left it so,
        // because a first parse writes Markdown only when it succeeds. Its retry must replace, not first-write.
        var doc = await CreateExtractedDocumentAsync();
        var failed = await _pipelineRunManager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _pipelineRunManager.FailAsync(doc, failed, "provider unavailable");
        StubGet(doc);

        await _appService.RetryPipelineAsync(doc.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentParseJobArgs>(a => a.DocumentId == doc.Id && a.IsReparse),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Retrying_A_Failed_First_Parse_Stays_A_First_Parse()
    {
        var doc = CreateDocument();
        var failed = await _pipelineRunManager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _pipelineRunManager.FailAsync(doc, failed, "provider unavailable");
        StubGet(doc);

        await _appService.RetryPipelineAsync(doc.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentParseJobArgs>(a => a.DocumentId == doc.Id && !a.IsReparse),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private Task AssertNothingQueuedAsync()
        => _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentParseJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());

    private void StubGet(Document doc)
    {
        // ReparseAsync and RetryPipelineAsync use GetAsync(includeDetails:false).
        _documentRepository.GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    // Persist Markdown through the manager's public CompleteParseAsync. SetMarkdown is internal
    // and tests do not call it directly. This simulates the "text extraction succeeded" precondition.
    private async Task<Document> CreateExtractedDocumentAsync()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _pipelineRunManager.CompleteParseAsync(doc, run, "# Sample\n\nbody", "Sample");
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

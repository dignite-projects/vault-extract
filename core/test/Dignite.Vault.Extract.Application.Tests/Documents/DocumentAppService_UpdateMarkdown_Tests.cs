using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Behavior tests for <see cref="DocumentAppService.UpdateMarkdownAsync"/> (#555): operator correction of
/// already-extracted <see cref="Document.Markdown"/>. Reuses <see cref="DocumentAppServiceReviewTestModule"/>
/// (same as <c>DocumentAppService_ExtractedFields_Tests</c>) for its default empty-list repository stubs, and
/// asserts the Reprocess=true enqueue the same way <c>DocumentAppService_Rerecognize_Tests</c> does — via
/// <see cref="IDocumentPipelineRunRepository.FindLatestByDocumentAndCodeAsync"/>.
/// </summary>
public class DocumentAppService_UpdateMarkdown_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_UpdateMarkdown_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task Reprocess_False_Updates_Markdown_And_Does_Not_Enqueue_Or_Publish()
    {
        var doc = await CreateClassifiedExtractedDocumentAsync();
        StubGet(doc);

        var dto = await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
        {
            Markdown = "# Doc\n\ncorrected body",
            Reprocess = false
        });

        doc.Markdown.ShouldBe("# Doc\n\ncorrected body");
        dto.Id.ShouldBe(doc.Id);

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Reprocess_True_Updates_Markdown_And_Enqueues_Field_Extraction()
    {
        var doc = await CreateClassifiedExtractedDocumentAsync();
        StubGet(doc);

        await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
        {
            Markdown = "# Doc\n\ncorrected body",
            Reprocess = true
        });

        doc.Markdown.ShouldBe("# Doc\n\ncorrected body");

        var newRun = await _runRepository.FindLatestByDocumentAndCodeAsync(doc.Id, VaultExtractPipelines.FieldExtraction);
        newRun.ShouldNotBeNull();
        newRun.Status.ShouldBe(PipelineRunStatus.Pending);

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentFieldExtractionJobArgs>(a =>
                a.DocumentId == doc.Id &&
                a.PipelineRunId == newRun.Id),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Throws_CannotCorrectContainerMarkdown_When_Document_Is_A_Container()
    {
        var doc = await CreateExtractedDocumentAsync();
        typeof(Document).GetProperty(nameof(Document.IsContainer))!.SetValue(doc, true);
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
            {
                Markdown = "# Doc\n\ncorrected body",
                Reprocess = false
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.CannotCorrectContainerMarkdown);
    }

    [Fact]
    public async Task Throws_InRecycleBin_When_Document_Is_Soft_Deleted()
    {
        var doc = await CreateExtractedDocumentAsync();
        doc.IsDeleted = true;
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
            {
                Markdown = "# Doc\n\ncorrected body",
                Reprocess = false
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.InRecycleBin);
    }

    [Fact]
    public async Task Throws_NotClassified_When_Unclassified_And_Reprocess_True()
    {
        var doc = await CreateExtractedDocumentAsync(); // DocumentTypeId stays null
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
            {
                Markdown = "# Doc\n\ncorrected body",
                Reprocess = true
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.NotClassified);
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Unclassified_And_Reprocess_False_Succeeds()
    {
        var doc = await CreateExtractedDocumentAsync(); // DocumentTypeId stays null
        StubGet(doc);

        var dto = await _appService.UpdateMarkdownAsync(doc.Id, new UpdateMarkdownInput
        {
            Markdown = "# Doc\n\ncorrected body",
            Reprocess = false
        });

        doc.Markdown.ShouldBe("# Doc\n\ncorrected body");
        doc.DocumentTypeId.ShouldBeNull();
        dto.Id.ShouldBe(doc.Id);
    }

    private void StubGet(Document doc)
    {
        // UpdateMarkdownAsync loads via FindWithFieldValuesAsync so the returned DTO carries field/warning details.
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    // Persist Markdown through the manager's public CompleteParseAsync -- SetMarkdown is internal and this
    // test project has no InternalsVisibleTo into Domain, matching DocumentAppService_Rerecognize_Tests.
    private async Task<Document> CreateExtractedDocumentAsync()
    {
        var doc = CreateDocument();
        var run = await _pipelineRunManager.StartAsync(doc, VaultExtractPipelines.Parse);
        await _pipelineRunManager.CompleteParseAsync(doc, run, "# Sample\n\nbody", "Sample");
        return doc;
    }

    // DocumentTypeId's setter is Domain-internal too; set it through reflection like
    // DocumentAppService_ExtractedFields_Tests.CreateClassifiedDocument does.
    private async Task<Document> CreateClassifiedExtractedDocumentAsync()
    {
        var doc = await CreateExtractedDocumentAsync();
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, Guid.NewGuid());
        return doc;
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

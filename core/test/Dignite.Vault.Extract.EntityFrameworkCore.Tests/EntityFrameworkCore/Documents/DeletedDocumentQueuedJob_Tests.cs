using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DeletedDocumentQueuedJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // The three jobs are resolved from DI; their workflows need the host's keyed chat clients. No test here gets
        // as far as an LLM call — the document is gone before the external phase — so bare substitutes suffice.
        context.Services.AddKeyedSingleton(VaultExtractConsts.TitleGeneratorChatClientKey, Substitute.For<IChatClient>());
        context.Services.AddKeyedSingleton(VaultExtractConsts.StructuredChatClientKey, Substitute.For<IChatClient>());
    }
}

/// <summary>
/// #662: a pipeline job whose document was deleted while the job waited in the queue ends and fails its run, instead
/// of rethrowing into ABP's retry loop. Before, the Begin phase's <c>GetAsync</c> threw <see cref="EntityNotFoundException"/>
/// out of the job without failing the run: ABP retried it until it abandoned it, and a document restored after that
/// kept a <c>Pending</c> run nothing would execute and <c>RetryPipelineAsync</c> would not accept. Real SQLite, so the
/// soft-delete filter is the real one.
/// </summary>
public class DeletedDocumentQueuedJob_Tests : VaultExtractTestBase<DeletedDocumentQueuedJobTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineJobScheduler _scheduler;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;

    public DeletedDocumentQueuedJob_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _scheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    [Theory]
    [InlineData(VaultExtractPipelines.Parse)]
    [InlineData(VaultExtractPipelines.Classification)]
    [InlineData(VaultExtractPipelines.FieldExtraction)]
    public async Task A_queued_job_for_a_soft_deleted_document_ends_and_fails_its_run(string pipelineCode)
    {
        var documentId = await ArrangeDocumentAsync();
        var runId = await QueueRunAsync(documentId, pipelineCode);
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId));
        _eventBus.ClearReceivedCalls();
        _backgroundJobManager.ClearReceivedCalls();

        await Should.NotThrowAsync(() => ExecuteJobAsync(pipelineCode, documentId, runId));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var run = await _runRepository.FindAsync(runId);
                run!.Status.ShouldBe(PipelineRunStatus.Failed);
                run.StatusMessage.ShouldBe(DocumentPipelineBackgroundJobBase<DocumentParseJobArgs>.DocumentDeletedRunMessage);

                var document = await _documentRepository.GetAsync(documentId);
                document.IsDeleted.ShouldBeTrue();
                document.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);
            }
        });

        // Nothing leaves the channel, and nothing is queued behind it.
        _eventBus.ReceivedCalls().ShouldBeEmpty();
        _backgroundJobManager.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restored_document_comes_back_failed_with_a_run_the_operator_can_retry()
    {
        var documentId = await ArrangeDocumentAsync();
        var runId = await QueueRunAsync(documentId, VaultExtractPipelines.Parse);
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId));
        await ExecuteJobAsync(VaultExtractPipelines.Parse, documentId, runId);

        await _appService.RestoreAsync(documentId);
        _backgroundJobManager.ClearReceivedCalls();

        await _appService.RetryPipelineAsync(documentId, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse });

        await WithUnitOfWorkAsync(async () =>
        {
            var retry = await _runRepository.FindLatestByDocumentAndCodeAsync(documentId, VaultExtractPipelines.Parse);
            retry!.Status.ShouldBe(PipelineRunStatus.Pending);
            retry.AttemptNumber.ShouldBe(2);
        });
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentParseJobArgs>(a => a.DocumentId == documentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_queued_job_for_a_permanently_deleted_document_ends_quietly()
    {
        var documentId = await ArrangeDocumentAsync();
        var runId = await QueueRunAsync(documentId, VaultExtractPipelines.Classification);
        await WithUnitOfWorkAsync(() => _documentRepository.HardDeleteAsync(documentId));

        await Should.NotThrowAsync(() => ExecuteJobAsync(VaultExtractPipelines.Classification, documentId, runId));
    }

    [Fact]
    public async Task A_document_deleted_mid_run_is_ended_on_the_retry()
    {
        // Deleted while the job's external phase runs: the Complete phase cannot load the document, so this attempt
        // still fails the way it always has, and the run is left Running. The retry ABP schedules then ends it.
        var documentId = await ArrangeDocumentAsync();
        var runId = await QueueRunAsync(documentId, VaultExtractPipelines.Parse);
        _blobContainer.GetAsync(Arg.Any<string>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _textExtractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId)).GetAwaiter().GetResult();
                return new TextExtractionResult { Markdown = "# Late", ProviderName = "ElBruno.MarkItDotNet" };
            });

        await Should.ThrowAsync<EntityNotFoundException>(() => ExecuteJobAsync(VaultExtractPipelines.Parse, documentId, runId));

        await Should.NotThrowAsync(() => ExecuteJobAsync(VaultExtractPipelines.Parse, documentId, runId));

        await WithUnitOfWorkAsync(async () =>
        {
            var run = await _runRepository.FindAsync(runId);
            run!.Status.ShouldBe(PipelineRunStatus.Failed);
            run.StatusMessage.ShouldBe(DocumentPipelineBackgroundJobBase<DocumentParseJobArgs>.DocumentDeletedRunMessage);
        });
    }

    private async Task<Guid> ArrangeDocumentAsync()
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => _documentRepository.InsertAsync(
            new Document(
                documentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{documentId:N}.txt",
                    uploadedByUserName: "test-user",
                    contentType: "text/plain",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 16,
                    originalFileName: "note.txt")),
            autoSave: true));
        return documentId;
    }

    private async Task<Guid> QueueRunAsync(Guid documentId, string pipelineCode)
    {
        var runId = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId);
            runId = (await _scheduler.QueueAsync(document, pipelineCode)).Id;
        });
        return runId;
    }

    private Task ExecuteJobAsync(string pipelineCode, Guid documentId, Guid runId) => pipelineCode switch
    {
        VaultExtractPipelines.Parse => GetRequiredService<DocumentParseBackgroundJob>().ExecuteAsync(
            new DocumentParseJobArgs { DocumentId = documentId, PipelineRunId = runId }),
        VaultExtractPipelines.Classification => GetRequiredService<DocumentClassificationBackgroundJob>().ExecuteAsync(
            new DocumentClassificationJobArgs { DocumentId = documentId, PipelineRunId = runId }),
        VaultExtractPipelines.FieldExtraction => GetRequiredService<DocumentFieldExtractionBackgroundJob>().ExecuteAsync(
            new DocumentFieldExtractionJobArgs { DocumentId = documentId, PipelineRunId = runId }),
        _ => throw new ArgumentOutOfRangeException(nameof(pipelineCode), pipelineCode, null)
    };
}

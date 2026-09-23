using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentReparseTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // Title generation is best-effort and swallows its failures, so a bare substitute falls back to the
        // rule-based extractor — which is what lets the tests predict the new title.
        context.Services.AddKeyedSingleton(
            VaultExtractConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
    }
}

/// <summary>
/// #660 integration tests for re-parse against the real SQLite DB: <c>DocumentAppService.ReparseAsync</c> queues a
/// text-extraction run marked as a re-parse, and <c>DocumentParseBackgroundJob</c> completes it by replacing every
/// parse output, withdrawing the sub-documents split from the old Markdown with their ledger rows, and queueing LLM
/// classification — before it completes its own run, so the document never derives Ready in between.
/// <para>
/// <see cref="IDistributedEventBus"/> is a substitute, so event assertions are call counts; outbox enrolment is a
/// framework guarantee. The EF test module disables unit-of-work transactions, so the rollback a transactional re-parse
/// completion gives in production is not exercised here; the empty-result refusal below is checked before anything
/// is written, which is why it holds either way.
/// </para>
/// </summary>
public class DocumentReparse_Tests : VaultExtractTestBase<DocumentReparseTestModule>
{
    private const string OldMarkdown = "# Old title\n\nInvoice A first\nInvoice B second";
    private const string NewMarkdown = "# New title\n\nInvoice A first\nInvoice B second\nInvoice C third";

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineRunManager _runManager;
    private readonly DocumentPipelineJobScheduler _scheduler;
    private readonly DocumentParseBackgroundJob _parseJob;
    private readonly DerivedDocumentSpawner _spawner;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;

    public DocumentReparse_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _runManager = GetRequiredService<DocumentPipelineRunManager>();
        _scheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _parseJob = GetRequiredService<DocumentParseBackgroundJob>();
        _spawner = GetRequiredService<DerivedDocumentSpawner>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
        // ReparseAsync refuses a document whose original file is not in storage; every file is there here.
        _blobContainer.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task Reparsing_A_Container_Replaces_Its_Text_And_Withdraws_Every_Sub_Document()
    {
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 2, figureChildren: 1);

        await ReparseAsync(arranged.DocumentId, NewMarkdown);

        await AssertReparsedAsync(arranged, withdrawnCount: 3);
    }

    [Fact]
    public async Task Reparsing_A_Concrete_Document_Withdraws_Its_Embedded_Sub_Documents_Too()
    {
        // The difference from a container→concrete reclassify, which keeps Figure children because the Markdown is
        // unchanged: here the text they were split from is gone.
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: false, textChildren: 0, figureChildren: 2);

        await ReparseAsync(arranged.DocumentId, NewMarkdown);

        await AssertReparsedAsync(arranged, withdrawnCount: 2);
    }

    [Fact]
    public async Task Reparsing_An_Upload_Declared_Document_Runs_The_Classifier()
    {
        // #623: an upload-declared type skips the LLM on the FIRST parse, completing classification synchronously as a
        // manual classification. A re-parse must not take that branch: only the classifier can say the new text is a
        // bundle, which is #623's own remedy for a declared type that really is one. So the classification run is
        // queued for the LLM, not completed, and no DocumentClassifiedEto is published by the re-parse itself.
        var arranged = await ArrangeProcessedDocumentAsync(
            asContainer: false, textChildren: 0, figureChildren: 0, declareType: true);

        await ReparseAsync(arranged.DocumentId, NewMarkdown);

        await WithUnitOfWorkAsync(async () =>
        {
            var classification = await _runRepository.FindLatestByDocumentAndCodeAsync(
                arranged.DocumentId, VaultExtractPipelines.Classification);
            classification!.Status.ShouldBe(PipelineRunStatus.Pending);
            classification.AttemptNumber.ShouldBe(2);
        });
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == arranged.DocumentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentClassifiedEto>());
    }

    [Fact]
    public async Task A_Redelivered_First_Parse_Job_Still_Refuses_To_Overwrite()
    {
        // The mode travels in the job args: a first-parse job arriving at a document that already has Markdown must
        // fail on the write-once guard, never take the replacing path.
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 2, figureChildren: 0);
        var runId = await QueueParseRunAsync(arranged.DocumentId, isReparse: false);
        StubExtraction(NewMarkdown);

        var ex = await Should.ThrowAsync<BusinessException>(() => _parseJob.ExecuteAsync(new DocumentParseJobArgs
        {
            DocumentId = arranged.DocumentId,
            PipelineRunId = runId
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.MarkdownIsImmutable);
        await AssertUntouchedAsync(arranged, runId);
    }

    [Fact]
    public async Task A_Reparse_That_Yields_No_Text_Keeps_The_Previous_Text_And_Sub_Documents()
    {
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 2, figureChildren: 0);
        await _appService.ReparseAsync(arranged.DocumentId);
        var args = LastEnqueued<DocumentParseJobArgs>();
        StubExtraction("   ");

        await Should.ThrowAsync<InvalidOperationException>(() => _parseJob.ExecuteAsync(args));

        await AssertUntouchedAsync(arranged, args.PipelineRunId!.Value);
        await WithUnitOfWorkAsync(async () =>
            (await _runRepository.FindLatestByDocumentAndCodeAsync(arranged.DocumentId, VaultExtractPipelines.Classification))!
                .AttemptNumber.ShouldBe(1));
    }

    [Fact]
    public async Task A_Sub_Document_Withdrawn_By_A_Reparse_Cannot_Be_Restored()
    {
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 2, figureChildren: 1);
        await ReparseAsync(arranged.DocumentId, NewMarkdown);

        foreach (var childId in arranged.ChildIds)
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _appService.RestoreAsync(childId));
            ex.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreSuperseded);
        }

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentRestoredEto>());
    }

    [Fact]
    public async Task A_Sub_Document_Already_In_The_Recycle_Bin_Cannot_Be_Restored_After_A_Reparse()
    {
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 2, figureChildren: 0);
        var binnedChildId = arranged.ChildIds[0];
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(binnedChildId));

        await ReparseAsync(arranged.DocumentId, NewMarkdown);

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.RestoreAsync(binnedChildId));
        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreSuperseded);
    }

    [Fact]
    public async Task A_Spawn_That_Loaded_Its_Row_Before_The_Reparse_Cannot_Mark_It_Spawned_Afterwards()
    {
        // A split was still spawning when the operator re-parsed: the spawner loaded a Pending row, the re-parse then
        // committed and deleted it, and only then does the spawner try to mark it Spawned. The write meets a row that
        // no longer exists and fails as a concurrency conflict; in production the spawner's transactional unit of work
        // (#481) rolls its sub-document insert back with it. The EF test module runs without transactions, so only the
        // conflict is asserted here.
        var arranged = await ArrangeProcessedDocumentAsync(asContainer: true, textChildren: 0, figureChildren: 0);
        var pendingSegmentId = await ArrangePendingSegmentAsync(arranged.DocumentId, "Invoice slice pending");
        await _appService.ReparseAsync(arranged.DocumentId);
        var args = LastEnqueued<DocumentParseJobArgs>();
        StubExtraction(NewMarkdown);

        await Should.ThrowAsync<AbpDbConcurrencyException>(() => _spawner.SpawnAsync<DocumentSegment>(
            arranged.DocumentId,
            tenantId: null,
            constituentKey: ContentKey("Invoice slice pending"),
            fileOrigin: null,
            ownerId: null,
            reloadClaimable: async () =>
            {
                var loaded = await _segmentRepository.FindAsync(pendingSegmentId);
                // The re-parse commits in its own units of work while the spawner still holds the loaded row.
                await _parseJob.ExecuteAsync(args);
                return loaded;
            },
            markSpawned: async (segment, derivedId) =>
            {
                segment.MarkSpawned(derivedId);
                await _segmentRepository.UpdateAsync(segment);
            }));

        await WithUnitOfWorkAsync(async () =>
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == arranged.DocumentId)).ShouldBeEmpty());
    }

    private async Task ReparseAsync(Guid documentId, string newMarkdown)
    {
        await _appService.ReparseAsync(documentId);
        var args = LastEnqueued<DocumentParseJobArgs>();
        args.IsReparse.ShouldBeTrue();

        StubExtraction(newMarkdown);
        await _parseJob.ExecuteAsync(args);
    }

    private async Task AssertReparsedAsync(ArrangedDocument arranged, int withdrawnCount)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(arranged.DocumentId);
            document.Markdown.ShouldBe(NewMarkdown);
            document.Title.ShouldBe("New title");
            document.IsSegmented.ShouldBeFalse();
            // Classification was queued before the parse run completed, so the document is still Processing.
            document.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);

            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == arranged.DocumentId)).ShouldBeEmpty();
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == arranged.DocumentId)).ShouldBeEmpty();

            var parse = await _runRepository.FindLatestByDocumentAndCodeAsync(arranged.DocumentId, VaultExtractPipelines.Parse);
            parse!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            parse.AttemptNumber.ShouldBe(2);

            var classification = await _runRepository.FindLatestByDocumentAndCodeAsync(
                arranged.DocumentId, VaultExtractPipelines.Classification);
            classification!.Status.ShouldBe(PipelineRunStatus.Pending);
            classification.AttemptNumber.ShouldBe(2);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                foreach (var childId in arranged.ChildIds)
                {
                    (await _documentRepository.GetAsync(childId)).IsDeleted.ShouldBeTrue();
                }
            }
        });

        await _eventBus.Received(withdrawnCount).PublishAsync(Arg.Any<DocumentDeletedEto>());
        foreach (var childId in arranged.ChildIds)
        {
            await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentDeletedEto>(e => e.DocumentId == childId));
        }

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentTextExtractedEto>(e => e.DocumentId == arranged.DocumentId));
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentReadyEto>());

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == arranged.DocumentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentCabinetSuggestionJobArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private async Task AssertUntouchedAsync(ArrangedDocument arranged, Guid failedRunId)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(arranged.DocumentId);
            document.Markdown.ShouldBe(OldMarkdown);
            document.Title.ShouldBe("Old title");
            document.IsSegmented.ShouldBeTrue();

            var children = await _documentRepository.GetListAsync(d => d.OriginDocumentId == arranged.DocumentId);
            children.Select(c => c.Id).ShouldBe(arranged.ChildIds, ignoreOrder: true);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == arranged.DocumentId))
                .Count.ShouldBe(arranged.ChildIds.Length);

            (await _runRepository.FindAsync(failedRunId))!.Status.ShouldBe(PipelineRunStatus.Failed);
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentDeletedEto>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentTextExtractedEto>());
    }

    /// <summary>
    /// A processed document: parsed (<see cref="OldMarkdown"/>), classified (a container, or a concrete document with
    /// field extraction done), marked segmented, and carrying Spawned sub-documents of each requested kind with their
    /// ledger rows. All key pipelines succeeded, so it is Ready. Event and job substitutes are cleared afterwards, so
    /// the tests see only what the re-parse does.
    /// </summary>
    private async Task<ArrangedDocument> ArrangeProcessedDocumentAsync(
        bool asContainer, int textChildren, int figureChildren, bool declareType = false)
    {
        var documentId = _guidGenerator.Create();
        var childIds = new Guid[textChildren + figureChildren];

        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(
                documentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{documentId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));
            if (declareType)
            {
                // #623: declared at upload, before the first parse writes Markdown; the type is operator-confirmed.
                var typeId = _guidGenerator.Create();
                await GetRequiredService<IRepository<DocumentType, Guid>>().InsertAsync(
                    new DocumentType(typeId, tenantId: null, "declared.type", "Declared type"), autoSave: true);
                document.DeclareDocumentType(typeId);
            }

            await _documentRepository.InsertAsync(document, autoSave: true);

            var parse = await _runManager.StartAsync(document, VaultExtractPipelines.Parse);
            await _runManager.CompleteParseAsync(document, parse, OldMarkdown, "Old title");

            if (asContainer)
            {
                document.MarkAsContainer();
            }

            var classification = await _runManager.StartAsync(document, VaultExtractPipelines.Classification);
            await _runManager.CompleteAsync(document, classification);

            if (!asContainer)
            {
                var fields = await _runManager.StartAsync(document, VaultExtractPipelines.FieldExtraction);
                await _runManager.CompleteAsync(document, fields);
            }

            document.MarkSegmented();
            await _documentRepository.UpdateAsync(document, autoSave: true);

            for (var i = 0; i < childIds.Length; i++)
            {
                var kind = i < textChildren ? DocumentSegmentKind.Text : DocumentSegmentKind.Figure;
                var sliceText = $"{kind} slice {i}";
                childIds[i] = _guidGenerator.Create();

                await _documentRepository.InsertAsync(
                    Document.CreateDerived(
                        childIds[i], tenantId: null, fileOrigin: null,
                        originDocumentId: documentId, originConstituentKey: ContentKey(sliceText), creatorId: null),
                    autoSave: true);

                var segment = new DocumentSegment(
                    _guidGenerator.Create(), tenantId: null, sourceDocumentId: documentId,
                    segmentKey: ContentKey(sliceText), sliceText: sliceText, ordinal: i, kind: kind);
                segment.MarkSpawned(childIds[i]);
                await _segmentRepository.InsertAsync(segment, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(documentId)).LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready));

        _eventBus.ClearReceivedCalls();
        _backgroundJobManager.ClearReceivedCalls();
        return new ArrangedDocument(documentId, childIds);
    }

    private async Task<Guid> ArrangePendingSegmentAsync(Guid sourceDocumentId, string sliceText)
    {
        var segmentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => _segmentRepository.InsertAsync(
            new DocumentSegment(
                segmentId, tenantId: null, sourceDocumentId: sourceDocumentId,
                segmentKey: ContentKey(sliceText), sliceText: sliceText, ordinal: 0, kind: DocumentSegmentKind.Text),
            autoSave: true));
        return segmentId;
    }

    private async Task<Guid> QueueParseRunAsync(Guid documentId, bool isReparse)
    {
        var runId = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId);
            runId = (await _scheduler.QueueAsync(document, VaultExtractPipelines.Parse, isReparse: isReparse)).Id;
        });
        return runId;
    }

    private void StubExtraction(string markdown)
    {
        _blobContainer.GetAsync(Arg.Any<string>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _textExtractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>())
            .Returns(new TextExtractionResult
            {
                Markdown = markdown,
                DetectedLanguage = "en",
                ProviderName = "ElBruno.MarkItDotNet"
            });
    }

    private T LastEnqueued<T>() where T : class
        => _backgroundJobManager.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<T>()
            .Last();

    private static string ContentKey(string sliceText)
        => ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sliceText));

    private sealed record ArrangedDocument(Guid DocumentId, Guid[] ChildIds);
}

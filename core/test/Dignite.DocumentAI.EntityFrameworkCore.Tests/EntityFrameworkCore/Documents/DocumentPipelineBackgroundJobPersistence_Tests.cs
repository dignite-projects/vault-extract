using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Cabinets;
using Dignite.DocumentAI.Documents.Figures;
using Dignite.DocumentAI.Documents.Pipelines;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
using Dignite.DocumentAI.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class DocumentPipelineBackgroundJobPersistenceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // DocumentTextExtractionBackgroundJob now depends on the title-generator keyed
        // IChatClient (see DocumentAIConsts.TitleGeneratorChatClientKey); register a
        // substitute so DI can construct the job. Title generation is best-effort and
        // its failures are swallowed, so the substitute returning null is fine.
        context.Services.AddKeyedSingleton(
            DocumentAIConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
    }
}

public class DocumentPipelineBackgroundJobPersistence_Tests
    : DocumentAITestBase<DocumentPipelineBackgroundJobPersistenceTestModule>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly DocumentTextExtractionBackgroundJob _textExtractionJob;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDocumentAppService _documentAppService;
    private readonly IRepository<DocumentFigure, Guid> _figureRepository;

    public DocumentPipelineBackgroundJobPersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _pipelineJobScheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _textExtractionJob = GetRequiredService<DocumentTextExtractionBackgroundJob>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<DocumentAIDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _figureRepository = GetRequiredService<IRepository<DocumentFigure, Guid>>();
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Persist_Run_Status_And_Queued_Classification_Run()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        StubExtraction("# Contract\n\nThis is a contract.", usedOcr: false);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        Guid classificationRunId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            // #216: after PipelineRun became an independent aggregate root, queries use runRepo.
            var textExtractionRun = await _runRepository.FindAsync(textExtractionRunId);
            var allRuns = await _runRepository.GetListByDocumentAsync(documentId);
            var classificationRuns = allRuns
                .Where(x => x.PipelineCode == DocumentAIPipelines.Classification)
                .ToList();

            textExtractionRun.ShouldNotBeNull();
            textExtractionRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
            classificationRuns.Count.ShouldBe(1);
            classificationRuns[0].Status.ShouldBe(PipelineRunStatus.Pending);
            classificationRunId = classificationRuns[0].Id;
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(x =>
                x.DocumentId == documentId &&
                x.PipelineRunId == classificationRunId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());

        // #265: after successful text extraction, fan out a "blank cabinet AI fallback" job unconditionally.
        // The write-once Markdown rule ensures the success path runs at most once per document; avoid an
        // AttemptNumber==1 gate so the "first attempt failed, retry succeeded" case is not missed.
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentCabinetSuggestionJobArgs>(x => x.DocumentId == documentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Queue_Classification_For_Ocr_Path()
    {
        // #196 contract lock: OCR does not perform pre-quality gating. Even poor recognition quality on the OCR
        // path still advances the document to classification instead of routing it to PendingReview.
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        StubExtraction("# Blurry Scan\n\nlow quality ocr text.", usedOcr: true);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            var allRuns = await _runRepository.GetListByDocumentAsync(documentId);

            allRuns.Single(r => r.Id == textExtractionRunId).Status.ShouldBe(PipelineRunStatus.Succeeded);
            allRuns.Count(x => x.PipelineCode == DocumentAIPipelines.Classification).ShouldBe(1);
            document.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(x => x.DocumentId == documentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Text_Extraction_Job_Archives_Native_Payload_And_Persists_Provenance()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        var payload = new NativePayload(new byte[] { 10, 20, 30, 40, 50 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        // Archive into blob storage with a stable per-document key and overwrite semantics.
        await _blobContainer.Received(1).SaveAsync(
            $"extraction-native/{documentId}",
            Arg.Any<Stream>(),
            overrideExisting: true,
            Arg.Any<CancellationToken>());

        // Reload through an independent UoW and new DbContext, deserializing the JSON column from DB, to verify
        // all three fields were written and the JSON round-trip is consistent.
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

            document.Language.ShouldBe("en");

            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.ProviderName.ShouldBe("PaddleOCR");

            var manifest = document.ExtractionMetadata.NativePayloadManifest;
            manifest.ShouldNotBeNull();
            manifest!.BlobName.ShouldBe($"extraction-native/{documentId}");
            manifest.ContentType.ShouldBe("application/json");
            manifest.SizeBytes.ShouldBe(5);
            manifest.SchemaName.ShouldBe("PaddleOCR/PP-StructureV3");
            // SHA-256 of {10,20,30,40,50}
            manifest.Sha256.ShouldBe(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 10, 20, 30, 40, 50 }))
                    .ToLowerInvariant());
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Persists_Null_Manifest_When_No_Native_Payload()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        StubExtraction("# Digital\n\nbody", usedOcr: false, nativePayload: null);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        // No payload -> do not write an archive blob. GetAsync still reads the original file blob, but SaveAsync
        // should not be called.
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            (await _runRepository.FindAsync(textExtractionRunId))!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Skips_Archive_When_Payload_Exceeds_Limit()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        // Payload exceeds the archive limit (DocumentConsts.MaxNativePayloadArchiveBytes) -> fail open: skip the
        // archive, set manifest to null, and keep text extraction successful. Use a real over-limit size instead
        // of changing global static state, avoiding mutable-state interference with parallel test classes.
        var oversized = new byte[(int)DocumentConsts.MaxNativePayloadArchiveBytes + 1];
        StubExtraction("# Scan\n\nbody", usedOcr: true,
            nativePayload: new NativePayload(oversized, "application/json", "PaddleOCR/PP-StructureV3"));

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        // Over limit -> do not write an archive blob.
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            (await _runRepository.FindAsync(textExtractionRunId))!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Skips_Archive_When_Payload_Has_Blank_Schema()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        // NativePayload contract does not require ContentType / SchemaName to be non-empty; future rich Markdown
        // or third-party providers may partially fill them.
        // Archiving must fail open: missing fields -> do not write blob, set manifest to null, and keep text
        // extraction successful. Auxiliary audit data must never throw and fail the main pipeline.
        StubExtraction("# Scan\n\nbody", usedOcr: true,
            nativePayload: new NativePayload(new byte[] { 1, 2, 3 }, contentType: "", schemaName: ""));

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            (await _runRepository.FindAsync(textExtractionRunId))!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Fails_Open_When_Blob_Archive_Throws()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        var payload = new NativePayload(new byte[] { 1, 2, 3 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        // Archive blob write failure -> fail open: set manifest to null and keep text extraction successful.
        _blobContainer.SaveAsync(
                Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("blob storage down"));

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            (await _runRepository.FindAsync(textExtractionRunId))!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.Markdown.ShouldBe("# Scan\n\nbody");
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task PermanentDelete_Removes_Archived_Native_Payload_Blob()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        var payload = new NativePayload(new byte[] { 7, 7, 7 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await _documentAppService.PermanentDeleteAsync(documentId);

        // Permanent delete removes the archive blob by the stable manifest key, alongside original-file blob deletion.
        await _blobContainer.Received(1).DeleteAsync(
            $"extraction-native/{documentId}", Arg.Any<CancellationToken>());
        await _blobContainer.Received(1).DeleteAsync(
            "blobs/test.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_Extraction_Job_Persists_Scenario_B_Candidate_Figures()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);

        var figureBytes = new byte[] { 9, 8, 7, 6 };
        StubExtraction("# Contract\n\nbody", usedOcr: false,
            figures: new[] { new Figure(figureBytes, "image/png", "INVOICE No. 42", pageNumber: 2) },
            figureOcrCount: 1);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        var contentHash = Sha256Hex(figureBytes);

        // The crop is persisted to blob under the content-hash key (overwrite semantics).
        await _blobContainer.Received(1).SaveAsync(
            $"figures/{documentId}/{contentHash}", Arg.Any<Stream>(), overrideExisting: true, Arg.Any<CancellationToken>());

        // One DocumentFigure candidate row carrying provenance + the content hash that doubles as OriginConstituentKey.
        await WithUnitOfWorkAsync(async () =>
        {
            var figures = await _figureRepository.GetListAsync(f => f.SourceDocumentId == documentId);
            var figure = figures.ShouldHaveSingleItem();
            figure.ContentHash.ShouldBe(contentHash);
            figure.CropBlobName.ShouldBe($"figures/{documentId}/{contentHash}");
            figure.ContentType.ShouldBe("image/png");
            figure.PageNumber.ShouldBe(2);
            figure.Transcription.ShouldBe("INVOICE No. 42");
            figure.RoutedDocumentId.ShouldBeNull();
            figure.Status.ShouldBe(DocumentFigureStatus.Pending);
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Deduplicates_Identical_Candidate_Figures()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);

        // Two embedded images with identical bytes -> one candidate (same content hash); the unique
        // (SourceDocumentId, ContentHash) index is the final guard, intra-batch de-dup avoids the collision.
        var bytes = new byte[] { 4, 4, 4 };
        StubExtraction("# Doc\n\nbody", usedOcr: false,
            figures: new[]
            {
                new Figure(bytes, "image/png", "same figure", pageNumber: 1),
                new Figure(bytes, "image/png", "same figure", pageNumber: 3)
            },
            figureOcrCount: 2);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var figures = await _figureRepository.GetListAsync(f => f.SourceDocumentId == documentId);
            figures.ShouldHaveSingleItem();
        });
    }

    [Fact]
    public async Task PermanentDelete_Removes_Candidate_Figure_Crop_Blobs()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);

        var figureBytes = new byte[] { 5, 5, 5, 5 };
        StubExtraction("# Contract\n\nbody", usedOcr: false,
            figures: new[] { new Figure(figureBytes, "image/png", "fig", pageNumber: 1) },
            figureOcrCount: 1);

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await _documentAppService.PermanentDeleteAsync(documentId);

        // The candidate crop blob is deleted alongside the original-file blob; its DocumentFigure row was
        // already removed by the FK CASCADE on hard delete.
        await _blobContainer.Received(1).DeleteAsync(
            $"figures/{documentId}/{Sha256Hex(figureBytes)}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_Complete_Phase_Reclaims_Orphaned_Figure_Crop_Blobs()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);

        var figureBytes = new byte[] { 1, 2, 3, 9 };
        StubExtraction("# Contract\n\nbody", usedOcr: false,
            figures: new[] { new Figure(figureBytes, "image/png", "fig", pageNumber: 1) },
            figureOcrCount: 1);

        // Force the Complete phase to throw AFTER the External phase wrote the crop blob: the cabinet-suggestion
        // fan-out enqueue runs inside CompleteRunAsync before the UoW commits, so the figure-row inserts roll back.
        _backgroundJobManager.EnqueueAsync(
                Arg.Any<DocumentCabinetSuggestionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>())
            .ThrowsAsync(new InvalidOperationException("complete-phase boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => _textExtractionJob.ExecuteAsync(
            new DocumentTextExtractionJobArgs { DocumentId = documentId, PipelineRunId = textExtractionRunId }));

        // The crop was written in the External phase; the Complete UoW threw before commit, so the catch path
        // reclaims the crop blob instead of leaking it. (Asserting the DocumentFigure rows rolled back is left to
        // production transaction semantics — the in-memory SQLite test harness does not roll back autoSaved
        // inserts; this test pins the blob-reclaim behavior the fix adds.)
        await _blobContainer.Received(1).DeleteAsync(
            $"figures/{documentId}/{Sha256Hex(figureBytes)}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_Extraction_Seeds_Derived_Document_From_Figure_Transcription()
    {
        var sourceId = _guidGenerator.Create();
        var derivedId = _guidGenerator.Create();
        var figureKey = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
        const string seed = "# Invoice\n\nTotal 100.00";

        Guid runId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            // The source document the figure belongs to (the figure FK requires it to exist).
            await _documentRepository.InsertAsync(CreateDocument(sourceId), autoSave: true);

            // The source figure whose transcription the derived document seeds from.
            await _figureRepository.InsertAsync(new DocumentFigure(
                _guidGenerator.Create(), tenantId: null, sourceDocumentId: sourceId,
                contentHash: figureKey, cropBlobName: $"figures/{sourceId}/{figureKey}",
                contentType: "image/png", transcription: seed, pageNumber: 1), autoSave: true);

            var derived = Document.CreateDerived(
                derivedId, tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{derivedId:N}.png", uploadedByUserName: "test-user",
                    contentType: "image/png", contentHash: figureKey, fileSize: 4, originalFileName: "figure.png"),
                originDocumentId: sourceId, originConstituentKey: figureKey);
            await _documentRepository.InsertAsync(derived, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(derived, DocumentAIPipelines.TextExtraction);
            runId = run.Id;
        });

        // Intentionally do NOT stub the extractor: the seeded path must not call it.
        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs { DocumentId = derivedId, PipelineRunId = runId });

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = await _documentRepository.GetAsync(derivedId, includeDetails: false);
            derived.Markdown.ShouldBe(seed); // seeded from the figure transcription, no OCR
        });

        await _textExtractor.DidNotReceive().ExtractAsync(
            Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    private async Task<Guid> ArrangeQueuedTextExtractionAsync(Guid documentId)
    {
        Guid textExtractionRunId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = CreateDocument(documentId);
            await _documentRepository.InsertAsync(document, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(document, DocumentAIPipelines.TextExtraction);
            textExtractionRunId = run.Id;
        });
        return textExtractionRunId;
    }

    // Stub callback assertion that external extraction work runs outside the ambient UoW, matching the
    // background-jobs.md short-UoW rule.
    private void StubExtraction(string markdown, bool usedOcr, NativePayload? nativePayload = null,
        IReadOnlyList<Figure>? figures = null, int figureOcrCount = 0)
    {
        _blobContainer.GetAsync(Arg.Any<string>())
            .Returns(Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _textExtractor.ExtractAsync(
                Arg.Any<Stream>(),
                Arg.Any<TextExtractionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWorkManager.Current.ShouldBeNull();
                return new TextExtractionResult
                {
                    Markdown = markdown,
                    DetectedLanguage = "en",
                    UsedOcr = usedOcr,
                    ProviderName = usedOcr ? "PaddleOCR" : "ElBruno.MarkItDotNet",
                    NativePayload = nativePayload,
                    Figures = figures,
                    FigureOcrCount = figureOcrCount
                };
            });
    }

    // Delegate to the production canonical hasher so the test asserts the figure crop key against the exact
    // hash convention the code under test uses, rather than a parallel hand-rolled copy that could drift (#306 review).
    private static string Sha256Hex(byte[] bytes) => ContentHasher.Sha256Hex(bytes);

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: "blobs/test.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

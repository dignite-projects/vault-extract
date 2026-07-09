using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentPipelineBackgroundJobPersistenceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // DocumentParseBackgroundJob now depends on the title-generator keyed
        // IChatClient (see VaultExtractConsts.TitleGeneratorChatClientKey); register a
        // substitute so DI can construct the job. Title generation is best-effort and
        // its failures are swallowed, so the substitute returning null is fine.
        context.Services.AddKeyedSingleton(
            VaultExtractConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
    }
}

public class DocumentPipelineBackgroundJobPersistence_Tests
    : VaultExtractTestBase<DocumentPipelineBackgroundJobPersistenceTestModule>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly DocumentParseBackgroundJob _textExtractionJob;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDocumentAppService _documentAppService;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;

    public DocumentPipelineBackgroundJobPersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _pipelineJobScheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _textExtractionJob = GetRequiredService<DocumentParseBackgroundJob>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Persist_Run_Status_And_Queued_Classification_Run()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        StubExtraction("# Contract\n\nThis is a contract.", usedOcr: false);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
                .Where(x => x.PipelineCode == VaultExtractPipelines.Classification)
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
    public async Task Text_Extraction_Job_Keeps_Figure_Markers_In_Egress_Markdown()
    {
        // #381: figure OCR transcriptions are bracketed with *[Image OCR]*…*[End OCR]* provenance markers that STAY in
        // Document.Markdown (the egress payload), MarkItDown-style — they are no longer stripped, and there is no
        // separate marked-Markdown artifact. Downstream (RAG / classification / egress) sees the OCR provenance, and
        // the pipeline reads the same inline markers to recognize the figure span.
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);

        var figureBody = "INVOICE No 42 Total 100";
        var marked = "# Contract\n\nBody before the figure.\n\n"
            + ImageOcrMarkup.Wrap(figureBody, pageNumber: 3)
            + "\n\nBody after the figure.";
        StubExtraction(marked, usedOcr: false);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        // (a) No separate marked-Markdown artifact is archived (and there is no native payload here either), so the
        // job writes no blob at all — the markers live in Document.Markdown itself.
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // (b) The persisted egress payload retains the markers verbatim AND the figure transcription body inline.
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(documentId, includeDetails: false);
            doc.Markdown.ShouldNotBeNull();
            var markdown = doc.Markdown!;
            ImageOcrMarkup.Contains(markdown).ShouldBeTrue();
            markdown.ShouldContain(figureBody);
            markdown.ShouldContain("*[Image OCR p:3]*");
            markdown.ShouldContain("*[End OCR]*");
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Queue_Classification_For_Ocr_Path()
    {
        // #196 contract lock: OCR does not perform pre-quality gating. Even poor recognition quality on the OCR
        // path still advances the document to classification instead of routing it to PendingReview.
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        StubExtraction("# Blurry Scan\n\nlow quality ocr text.", usedOcr: true);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            var allRuns = await _runRepository.GetListByDocumentAsync(documentId);

            allRuns.Single(r => r.Id == textExtractionRunId).Status.ShouldBe(PipelineRunStatus.Succeeded);
            allRuns.Count(x => x.PipelineCode == VaultExtractPipelines.Classification).ShouldBe(1);
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        var payload = new NativePayload(new byte[] { 10, 20, 30, 40, 50 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        StubExtraction("# Digital\n\nbody", usedOcr: false, nativePayload: null);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        // Payload exceeds the archive limit (DocumentConsts.MaxNativePayloadArchiveBytes) -> fail open: skip the
        // archive, set manifest to null, and keep text extraction successful. Use a real over-limit size instead
        // of changing global static state, avoiding mutable-state interference with parallel test classes.
        var oversized = new byte[(int)DocumentConsts.MaxNativePayloadArchiveBytes + 1];
        StubExtraction("# Scan\n\nbody", usedOcr: true,
            nativePayload: new NativePayload(oversized, "application/json", "PaddleOCR/PP-StructureV3"));

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        // NativePayload contract does not require ContentType / SchemaName to be non-empty; future rich Markdown
        // or third-party providers may partially fill them.
        // Archiving must fail open: missing fields -> do not write blob, set manifest to null, and keep text
        // extraction successful. Auxiliary audit data must never throw and fail the main pipeline.
        StubExtraction("# Scan\n\nbody", usedOcr: true,
            nativePayload: new NativePayload(new byte[] { 1, 2, 3 }, contentType: "", schemaName: ""));

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        var payload = new NativePayload(new byte[] { 1, 2, 3 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        // Archive blob write failure -> fail open: set manifest to null and keep text extraction successful.
        _blobContainer.SaveAsync(
                Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("blob storage down"));

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
        var textExtractionRunId = await ArrangeQueuedParseAsync(documentId);
        var payload = new NativePayload(new byte[] { 7, 7, 7 }, "application/json", "PaddleOCR/PP-StructureV3");
        StubExtraction("# Scan\n\nbody", usedOcr: true, nativePayload: payload);

        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs
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
    public async Task Text_Extraction_Seeds_Derived_Document_From_Segment_Slice()
    {
        // #371: figure routing and born-digital segmentation are unified into one DocumentSegment ledger keyed by
        // the SHA-256 of the clean span text (= OriginConstituentKey). A derived sub-document seeds its Markdown
        // from its source segment's SliceText instead of re-extracting it — whether the span was a Figure (an
        // embedded image's OCR transcription) or a Text constituent of a container bundle. This pins the Figure-kind
        // seeding path, the successor to the old DocumentFigure-transcription seeding.
        var sourceId = _guidGenerator.Create();
        var derivedId = _guidGenerator.Create();
        var segmentKey = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
        const string seed = "# Invoice\n\nTotal 100.00";

        Guid runId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            // The source document the segment belongs to (the segment FK requires it to exist).
            await _documentRepository.InsertAsync(CreateDocument(sourceId), autoSave: true);

            // The source segment whose slice text the derived document seeds from. Figure-kind: the span was an
            // embedded image whose transcription seeds the spawned sub-document; PageNumber is a recovery anchor.
            await _segmentRepository.InsertAsync(new DocumentSegment(
                _guidGenerator.Create(), tenantId: null, sourceDocumentId: sourceId,
                segmentKey: segmentKey, sliceText: seed, ordinal: 0,
                kind: DocumentSegmentKind.Figure, pageNumber: 2), autoSave: true);

            // A derived sub-document carries NO file of its own (FileOrigin null): the markdown-slice split does not
            // give children a source file. So the parse job MUST seed Markdown from the segment slice and never touch
            // a blob — passing null here exercises the real production shape and locks the null-FileOrigin parse path
            // (a non-null FileOrigin would mask a regression where BeginRunAsync dereferences FileOrigin.BlobName).
            var derived = Document.CreateDerived(
                derivedId, tenantId: null,
                fileOrigin: null,
                originDocumentId: sourceId, originConstituentKey: segmentKey);
            await _documentRepository.InsertAsync(derived, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(derived, VaultExtractPipelines.Parse);
            runId = run.Id;
        });

        // Intentionally do NOT stub the extractor: the seeded path must not call it.
        await _textExtractionJob.ExecuteAsync(new DocumentParseJobArgs { DocumentId = derivedId, PipelineRunId = runId });

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = await _documentRepository.GetAsync(derivedId, includeDetails: false);
            derived.Markdown.ShouldBe(seed); // seeded from the segment slice, no OCR
            derived.FileOrigin.ShouldBeNull(); // a split sub-document has no source file of its own

            // Kind / PageNumber round-trip through the DB on the source segment row.
            var segment = (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == sourceId))
                .ShouldHaveSingleItem();
            segment.Kind.ShouldBe(DocumentSegmentKind.Figure);
            segment.PageNumber.ShouldBe(2);
            segment.SegmentKey.ShouldBe(segmentKey);
        });

        await _textExtractor.DidNotReceive().ExtractAsync(
            Arg.Any<Stream>(), Arg.Any<TextExtractionContext>(), Arg.Any<CancellationToken>());
    }

    private async Task<Guid> ArrangeQueuedParseAsync(Guid documentId)
    {
        Guid textExtractionRunId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = CreateDocument(documentId);
            await _documentRepository.InsertAsync(document, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(document, VaultExtractPipelines.Parse);
            textExtractionRunId = run.Id;
        });
        return textExtractionRunId;
    }

    // Stub callback assertion that external extraction work runs outside the ambient UoW, matching the
    // background-jobs.md short-UoW rule.
    private void StubExtraction(string markdown, bool usedOcr, NativePayload? nativePayload = null,
        int figureOcrCount = 0)
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
                    FigureOcrCount = figureOcrCount
                };
            });
    }

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

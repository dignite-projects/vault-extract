using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Dignite.Paperbase.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

[DependsOn(typeof(PaperbaseEntityFrameworkCoreTestModule))]
public class DocumentPipelineBackgroundJobPersistenceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // DocumentTextExtractionBackgroundJob now depends on the title-generator keyed
        // IChatClient (see PaperbaseAIConsts.TitleGeneratorChatClientKey); register a
        // substitute so DI can construct the job. Title generation is best-effort and
        // its failures are swallowed, so the substitute returning null is fine.
        context.Services.AddKeyedSingleton(
            PaperbaseAIConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
    }
}

public class DocumentPipelineBackgroundJobPersistence_Tests
    : PaperbaseTestBase<DocumentPipelineBackgroundJobPersistenceTestModule>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly DocumentTextExtractionBackgroundJob _textExtractionJob;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDocumentAppService _documentAppService;

    public DocumentPipelineBackgroundJobPersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _pipelineJobScheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _textExtractionJob = GetRequiredService<DocumentTextExtractionBackgroundJob>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<PaperbaseDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
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
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            var textExtractionRun = document.GetRun(textExtractionRunId);
            var classificationRuns = document.PipelineRuns
                .Where(x => x.PipelineCode == PaperbasePipelines.Classification)
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
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Queue_Classification_For_Ocr_Path()
    {
        // #196 契约固化：OCR 不做事前质量门控。即便走 OCR 路径识别质量差，
        // 文档也照常推进到 classification，不被路由到 PendingReview。
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
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);

            document.GetRun(textExtractionRunId)!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.PipelineRuns
                .Count(x => x.PipelineCode == PaperbasePipelines.Classification)
                .ShouldBe(1);
            document.ReviewStatus.ShouldBe(DocumentReviewStatus.None);
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

        // 归档进 blob：稳定 per-document key + overwrite。
        await _blobContainer.Received(1).SaveAsync(
            $"extraction-native/{documentId}",
            Arg.Any<Stream>(),
            overrideExisting: true,
            Arg.Any<CancellationToken>());

        // 重新加载（独立 UoW → 新 DbContext → 从 DB 反序列化 JSON 列）验证三字段落值 + JSON 往返一致。
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

        // 无 payload → 不写归档 blob（GetAsync 读原始文件 blob 仍发生，但 SaveAsync 不应被调用）。
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            document.GetRun(textExtractionRunId)!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Skips_Archive_When_Payload_Exceeds_Limit()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        // payload 超过归档上限（DocumentConsts.MaxNativePayloadArchiveBytes）→ fail-open：跳过归档、manifest 置 null、
        // 文本提取仍成功。用真实超限大小而非改全局 static，避免与并行测试类共享可变状态串扰。
        var oversized = new byte[(int)DocumentConsts.MaxNativePayloadArchiveBytes + 1];
        StubExtraction("# Scan\n\nbody", usedOcr: true,
            nativePayload: new NativePayload(oversized, "application/json", "PaddleOCR/PP-StructureV3"));

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        // 超限 → 不写归档 blob。
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            document.GetRun(textExtractionRunId)!.Status.ShouldBe(PipelineRunStatus.Succeeded);
            document.ExtractionMetadata.ShouldNotBeNull();
            document.ExtractionMetadata!.NativePayloadManifest.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Text_Extraction_Job_Skips_Archive_When_Payload_Has_Blank_Schema()
    {
        var documentId = _guidGenerator.Create();
        var textExtractionRunId = await ArrangeQueuedTextExtractionAsync(documentId);
        // NativePayload 契约不强制 ContentType/SchemaName 非空（未来 rich Markdown / 第三方 provider 可能半填）。
        // 归档须 fail-open：缺字段 → 不写 blob、manifest 置 null、文本提取仍成功，绝不因辅助审计抛异常打挂主 pipeline。
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
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            document.GetRun(textExtractionRunId)!.Status.ShouldBe(PipelineRunStatus.Succeeded);
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

        // 归档 blob 写失败 → fail-open：manifest 置 null，文本提取仍成功。
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
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            document.GetRun(textExtractionRunId)!.Status.ShouldBe(PipelineRunStatus.Succeeded);
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

        // 永久删除按 manifest 的稳定 key 删归档 blob（与删原始文件 blob 并列）。
        await _blobContainer.Received(1).DeleteAsync(
            $"extraction-native/{documentId}", Arg.Any<CancellationToken>());
        await _blobContainer.Received(1).DeleteAsync(
            "blobs/test.pdf", Arg.Any<CancellationToken>());
    }

    private async Task<Guid> ArrangeQueuedTextExtractionAsync(Guid documentId)
    {
        Guid textExtractionRunId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = CreateDocument(documentId);
            await _documentRepository.InsertAsync(document, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.TextExtraction);
            textExtractionRunId = run.Id;
        });
        return textExtractionRunId;
    }

    // stub 回调内断言外部提取工作不在 ambient UoW 下运行（background-jobs.md 短 UoW 规则）。
    private void StubExtraction(string markdown, bool usedOcr, NativePayload? nativePayload = null)
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
                    NativePayload = nativePayload
                };
            });
    }

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            originalFileBlobName: "blobs/test.pdf",
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

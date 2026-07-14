using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

public class DocumentPipelineRunAggregatePersistence_Tests
    : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentPipelineRunAggregatePersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Independent_Aggregate_Persists_New_Run_For_Document()
    {
        var documentId = _guidGenerator.Create();
        Guid runId = default;

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            // #216: after PipelineRun was split into an independent aggregate root, GetAsync no longer
            // eager-loads runs; Manager.QueueAsync inserts through runRepo.
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            var run = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Classification);
            runId = run.Id;

            await _documentRepository.UpdateAsync(document, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            // Query directly through runRepo, no longer through the Document aggregate root, verifying the
            // independent aggregate-root persistence path.
            var persistedRun = await _runRepository.FindAsync(runId);
            persistedRun.ShouldNotBeNull();
            persistedRun.DocumentId.ShouldBe(documentId);
            persistedRun.PipelineCode.ShouldBe(VaultExtractPipelines.Classification);
            persistedRun.Status.ShouldBe(PipelineRunStatus.Pending);
        });
    }

    [Fact]
    public async Task GetLatestRunsByCodes_Surfaces_Unflushed_Run_In_Same_Uow()
    {
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            // Insert a run in the same UoW with autoSave:false and intentionally do not flush it. DB-side queries
            // cannot see the uncommitted row because it is not in the database, and the EF identity map cannot
            // materialize it from the query. Only GetLatestRunsByCodesAsync can see it by merging Added entries
            // from the change tracker. This locks #216 follow-up #1: removing that merge foreach in the repository
            // makes this test fail, because DeriveLifecycle falls back to the stale-view bug that cannot see
            // unflushed runs in the same UoW.
            var run = new DocumentPipelineRun(
                _guidGenerator.Create(),
                documentId,
                tenantId: null,
                VaultExtractPipelines.Classification,
                attemptNumber: 1);
            await _runRepository.InsertAsync(run, autoSave: false);

            var latest = await _runRepository.GetLatestRunsByCodesAsync(
                documentId,
                new[] { VaultExtractPipelines.Classification });

            latest.ShouldContainKey(VaultExtractPipelines.Classification);
            latest[VaultExtractPipelines.Classification].Id.ShouldBe(run.Id);
            latest[VaultExtractPipelines.Classification].Status.ShouldBe(PipelineRunStatus.Pending);
        });
    }

    [Fact]
    public async Task GetLatestRunsByCodes_Returns_Latest_Attempt_For_Each_Requested_Code()
    {
        var documentId = _guidGenerator.Create();
        var latestParseRunId = _guidGenerator.Create();
        var classificationRunId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
            await _runRepository.InsertAsync(
                NewRun(documentId, VaultExtractPipelines.Parse, attemptNumber: 1),
                autoSave: false);
            await _runRepository.InsertAsync(
                new DocumentPipelineRun(
                    latestParseRunId,
                    documentId,
                    tenantId: null,
                    VaultExtractPipelines.Parse,
                    attemptNumber: 2),
                autoSave: false);
            await _runRepository.InsertAsync(
                new DocumentPipelineRun(
                    classificationRunId,
                    documentId,
                    tenantId: null,
                    VaultExtractPipelines.Classification,
                    attemptNumber: 1),
                autoSave: false);
            await _runRepository.InsertAsync(
                NewRun(documentId, VaultExtractPipelines.FieldExtraction, attemptNumber: 1),
                autoSave: false);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var latest = await _runRepository.GetLatestRunsByCodesAsync(
                documentId,
                new[]
                {
                    VaultExtractPipelines.Parse,
                    VaultExtractPipelines.Classification
                });

            latest.Count.ShouldBe(2);
            latest[VaultExtractPipelines.Parse].Id.ShouldBe(latestParseRunId);
            latest[VaultExtractPipelines.Parse].AttemptNumber.ShouldBe(2);
            latest[VaultExtractPipelines.Classification].Id.ShouldBe(classificationRunId);
            latest.ShouldNotContainKey(VaultExtractPipelines.FieldExtraction);
        });
    }

    [Fact]
    public async Task GetLatestRunsByCodes_Surfaces_Unflushed_Modified_Run_In_Same_Uow()
    {
        var documentId = _guidGenerator.Create();
        var runId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
            await _runRepository.InsertAsync(
                new DocumentPipelineRun(
                    runId,
                    documentId,
                    tenantId: null,
                    VaultExtractPipelines.Classification,
                    attemptNumber: 1),
                autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var run = await _runRepository.GetAsync(runId);
            run.MarkRunning(DateTime.UtcNow);
            await _runRepository.UpdateAsync(run, autoSave: false);

            var latest = await _runRepository.GetLatestRunsByCodesAsync(
                documentId,
                new[] { VaultExtractPipelines.Classification });

            latest[VaultExtractPipelines.Classification].Id.ShouldBe(runId);
            latest[VaultExtractPipelines.Classification].Status.ShouldBe(PipelineRunStatus.Running);
        });
    }

    [Fact]
    public async Task GetLatestRunsByCodes_Returns_Empty_For_Empty_Code_Set()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var latest = await _runRepository.GetLatestRunsByCodesAsync(
                _guidGenerator.Create(),
                Array.Empty<string>());

            latest.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task InsertNewAttempt_Translates_Unique_Collision_To_RetryInProgress()
    {
        // #239: when (DocumentId, PipelineCode, AttemptNumber) hits the unique index, the repository catches the
        // provider-independent DbUpdateException type, without sniffing messages or error codes, and translates it
        // to RetryInProgress. This uses a real SQLite UNIQUE constraint to trigger the conflict, verifying the
        // translation path end to end without relying on any SQL Server-specific error text.
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            await _runRepository.InsertNewAttemptAsync(NewRun(documentId, attemptNumber: 1));
        });

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                // Another run with the same (doc, pipeline, attempt) but a new Id: the loser side of concurrent retry.
                await _runRepository.InsertNewAttemptAsync(NewRun(documentId, attemptNumber: 1));
            });
        });

        ex.Code.ShouldBe(VaultExtractErrorCodes.Pipeline.RetryInProgress);
    }

    private DocumentPipelineRun NewRun(
        Guid documentId,
        string pipelineCode = VaultExtractPipelines.Classification,
        int attemptNumber = 1)
    {
        return new DocumentPipelineRun(
            _guidGenerator.Create(),
            documentId,
            tenantId: null,
            pipelineCode,
            attemptNumber);
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

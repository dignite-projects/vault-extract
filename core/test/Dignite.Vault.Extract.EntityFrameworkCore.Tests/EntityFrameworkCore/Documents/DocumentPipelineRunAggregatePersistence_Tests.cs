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
    : ExtractEntityFrameworkCoreTestBase
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
            var run = await _pipelineRunManager.QueueAsync(document, ExtractPipelines.Classification);
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
            persistedRun.PipelineCode.ShouldBe(ExtractPipelines.Classification);
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
            // Insert a run in the same UoW with autoSave:false and intentionally do not flush it. DB-side GroupBy
            // cannot see the uncommitted row because it is not in the database, and the EF identity map cannot
            // materialize it from the query. Only GetLatestRunsByCodesAsync can see it by merging Added entries
            // from the change tracker. This locks #216 follow-up #1: removing that merge foreach in the repository
            // makes this test fail, because DeriveLifecycle falls back to the stale-view bug that cannot see
            // unflushed runs in the same UoW.
            var run = new DocumentPipelineRun(
                _guidGenerator.Create(),
                documentId,
                tenantId: null,
                ExtractPipelines.Classification,
                attemptNumber: 1);
            await _runRepository.InsertAsync(run, autoSave: false);

            var latest = await _runRepository.GetLatestRunsByCodesAsync(
                documentId,
                new[] { ExtractPipelines.Classification });

            latest.ShouldContainKey(ExtractPipelines.Classification);
            latest[ExtractPipelines.Classification].Id.ShouldBe(run.Id);
            latest[ExtractPipelines.Classification].Status.ShouldBe(PipelineRunStatus.Pending);
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

        ex.Code.ShouldBe(ExtractErrorCodes.Pipeline.RetryInProgress);
    }

    private DocumentPipelineRun NewRun(Guid documentId, int attemptNumber)
    {
        return new DocumentPipelineRun(
            _guidGenerator.Create(),
            documentId,
            tenantId: null,
            ExtractPipelines.Classification,
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Extract.Documents;
using Dignite.Extract.Documents.DocumentTypes;
using Dignite.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Integration tests for bulk reprocessing (#289) repository scope queries
/// (<see cref="IDocumentRepository.CountForReprocessingAsync"/> /
/// <see cref="IDocumentRepository.GetIdsForReprocessingAsync"/>) plus <c>field-extraction</c> pipeline
/// lifecycle neutrality.
/// </summary>
public class DocumentReprocessing_Tests : ExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IGuidGenerator _guidGenerator;

    // Stable Ids keep assertions readable. GetIds orders by Id; assertions compare sets and do not depend on
    // concrete order.
    private static readonly Guid TypeAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TypeBId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public DocumentReprocessing_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Scope_By_Type_Excludes_NeverExtracted_And_SoftDeleted()
    {
        var ids = await SeedAsync();

        var count = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountForReprocessingAsync(TypeAId, withReason: null, excludeManuallyConfirmed: false));
        // typeA with text: d1(auto) + d2(reviewed). d4 has no markdown and d6 is soft-deleted, so both are excluded.
        count.ShouldBe(2);

        var pageIds = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetIdsForReprocessingAsync(TypeAId, null, false, afterId: null, maxCount: 100));
        pageIds.ShouldBe(new[] { ids.D1, ids.D2 }, ignoreOrder: true);
    }

    [Fact]
    public async Task Exclude_Manually_Confirmed_Drops_Reviewed_Documents()
    {
        var ids = await SeedAsync();

        var count = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountForReprocessingAsync(TypeAId, withReason: null, excludeManuallyConfirmed: true));
        // Protect manual confirmation: d2(Reviewed) is excluded, leaving only d1.
        count.ShouldBe(1);

        var pageIds = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetIdsForReprocessingAsync(TypeAId, null, true, null, 100));
        pageIds.ShouldBe(new[] { ids.D1 });
    }

    [Fact]
    public async Task PendingReview_Scope_Returns_Only_PendingReview_Documents()
    {
        var ids = await SeedAsync();

        var count = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountForReprocessingAsync(
                documentTypeId: null, withReason: DocumentReviewReasons.UnresolvedClassification, excludeManuallyConfirmed: false));
        count.ShouldBe(1);

        var pageIds = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetIdsForReprocessingAsync(null, DocumentReviewReasons.UnresolvedClassification, false, null, 100));
        pageIds.ShouldBe(new[] { ids.D5 });
    }

    [Fact]
    public async Task AllDocuments_Scope_Counts_Every_TextExtracted_Active_Document()
    {
        await SeedAsync();

        var count = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountForReprocessingAsync(null, null, false));
        // d1, d2, d3, d5 have text and are active. d4 has no markdown and d6 is soft-deleted, so both are excluded.
        count.ShouldBe(4);
    }

    [Fact]
    public async Task Keyset_Pagination_Returns_Every_Id_Once_Across_Batches()
    {
        await SeedAsync();

        var collected = new List<Guid>();
        Guid? cursor = null;
        // batchSize=1 forces multiple batches and verifies the chained cursor has no duplicates or gaps.
        for (var guard = 0; guard < 50; guard++)
        {
            var batch = await WithUnitOfWorkAsync(() =>
                _documentRepository.GetIdsForReprocessingAsync(null, null, false, cursor, maxCount: 1));
            if (batch.Count == 0)
            {
                break;
            }

            collected.AddRange(batch);
            cursor = batch[^1];
        }

        collected.Count.ShouldBe(4);
        collected.Distinct().Count().ShouldBe(4); // No duplicates.
    }

    // #411: field-extraction became a KEY pipeline so the duplicate check can gate Ready. This reverses the former
    // lifecycle-neutral property: Ready is withheld until field extraction succeeds, and a re-extraction of an
    // already-Ready document bounces Ready -> Processing -> Ready (downstream absorbs the re-fired DocumentReadyEto
    // via EventTime idempotency).
    [Fact]
    public async Task FieldExtraction_Gates_Ready_And_ReExtraction_Bounces_Lifecycle()
    {
        var documentId = _guidGenerator.Create();

        // text-extraction + classification succeeded (a type assigned), but field extraction has not run yet.
        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            var doc = NewDocument(documentId);
            doc.SetMarkdown("# Body");
            doc.ApplyAutomaticClassificationResult(TypeAId, 0.99);
            await _documentRepository.InsertAsync(doc, autoSave: true);

            var te = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Parse);
            await _pipelineRunManager.CompleteAsync(doc, te);
            var cls = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.Classification);
            await _pipelineRunManager.CompleteAsync(doc, cls);
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        // Ready is withheld until the (now key) field-extraction pipeline succeeds.
        (await ReloadLifecycleAsync(documentId)).ShouldBe(DocumentLifecycleStatus.Processing);

        // First field-extraction run completes -> Ready.
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(documentId);
            var fe = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.FieldExtraction);
            await _pipelineRunManager.CompleteAsync(doc, fe);
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        (await ReloadLifecycleAsync(documentId)).ShouldBe(DocumentLifecycleStatus.Ready);

        // A re-extraction run bounces an already-Ready document Ready -> Processing (while Running) -> Ready.
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(documentId);
            var fe = await _pipelineRunManager.StartAsync(doc, ExtractPipelines.FieldExtraction);
            // StartAsync ran Queue(Pending) -> Begin(Running); the new run is not yet Succeeded, so Ready is withdrawn.
            doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
            await _pipelineRunManager.CompleteAsync(doc, fe);
            doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        (await ReloadLifecycleAsync(documentId)).ShouldBe(DocumentLifecycleStatus.Ready);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private sealed record SeededIds(Guid D1, Guid D2, Guid D3, Guid D4, Guid D5, Guid D6);

    private async Task<SeededIds> SeedAsync()
    {
        var ids = new SeededIds(
            _guidGenerator.Create(), _guidGenerator.Create(), _guidGenerator.Create(),
            _guidGenerator.Create(), _guidGenerator.Create(), _guidGenerator.Create());

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await EnsureTypeAsync(TypeBId, "type.b");

            // d1: typeA, has text, automatic classification (None)
            await InsertAsync(ids.D1, markdown: "# d1", d => d.ApplyAutomaticClassificationResult(TypeAId, 0.9));
            // d2: typeA, has text, manually confirmed (Reviewed)
            await InsertAsync(ids.D2, markdown: "# d2", d => d.ConfirmClassification(TypeAId));
            // d3: typeB, has text, automatic classification (None)
            await InsertAsync(ids.D3, markdown: "# d3", d => d.ApplyAutomaticClassificationResult(TypeBId, 0.8));
            // d4: typeA, no markdown (never extracted), should be excluded
            await InsertAsync(ids.D4, markdown: null, d => d.ApplyAutomaticClassificationResult(TypeAId, 0.9));
            // d5: no type, has text, pending review (PendingReview)
            await InsertAsync(ids.D5, markdown: "# d5", d => d.RequestClassificationReview());
            // d6: typeA, has text, automatic classification, soft-deleted, should be excluded
            await InsertAsync(ids.D6, markdown: "# d6", d => d.ApplyAutomaticClassificationResult(TypeAId, 0.9));
            await _documentRepository.DeleteAsync(ids.D6); // soft delete
        });

        return ids;
    }

    private async Task InsertAsync(Guid id, string? markdown, Action<Document> mutate)
    {
        var doc = NewDocument(id);
        if (markdown != null)
        {
            doc.SetMarkdown(markdown);
        }
        mutate(doc);
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private async Task EnsureTypeAsync(Guid id, string code)
    {
        if (await _documentTypeRepository.FindAsync(id) == null)
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(id, tenantId: null, typeCode: code, displayName: code), autoSave: true);
        }
    }

    private async Task<DocumentLifecycleStatus> ReloadLifecycleAsync(Guid id)
    {
        return await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(id)).LifecycleStatus);
    }

    private static Document NewDocument(Guid id) =>
        new(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
}

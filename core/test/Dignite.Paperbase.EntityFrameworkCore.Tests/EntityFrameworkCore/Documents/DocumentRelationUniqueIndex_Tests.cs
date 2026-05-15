using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Volo.Abp.Data;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// Issue #158 (Y2) regression guard: DocumentRelation has a unique filtered index on
/// (TenantId, SourceDocumentId, TargetDocumentId) filtered to IsDeleted = 0. This defends
/// against duplicate AiSuggested rows from concurrent RelationDiscovery runs (event-bus
/// duplicate delivery, Hangfire retry).
///
/// <para>
/// <strong>Tests use a non-null tenantId.</strong> SQL standard says NULL is not equal to NULL
/// in UNIQUE constraints; SQL Server (pre-2022), PostgreSQL (pre-15), and SQLite all enforce this.
/// In host (single-tenant) mode where all rows have TenantId=NULL, the DB-level constraint does
/// NOT catch duplicates — see EF mapping comment. Multi-tenant deployments (non-null TenantId)
/// are fully protected; that's what these tests verify. Tests wrap operations in
/// CurrentTenant.Change(TestTenantId) so the ambient IMultiTenant filter doesn't hide the
/// inserted rows from subsequent reads.
/// </para>
/// </summary>
public class DocumentRelationUniqueIndex_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentTenant _currentTenant;
    private static readonly Guid TestTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public DocumentRelationUniqueIndex_Tests()
    {
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task Inserting_Same_Live_Pair_Twice_Throws()
    {
        var sourceId = _guidGenerator.Create();
        var targetId = _guidGenerator.Create();

        await WithTenantUowAsync(async () =>
        {
            await _relationRepository.InsertAsync(
                BuildRelation(sourceId, targetId), autoSave: true);
        });

        // Second insert with the same (TenantId, Source, Target) must violate the unique index.
        await Should.ThrowAsync<DbUpdateException>(async () =>
        {
            await WithTenantUowAsync(async () =>
            {
                await _relationRepository.InsertAsync(
                    BuildRelation(sourceId, targetId), autoSave: true);
            });
        });
    }

    [Fact]
    public async Task Inserting_Reverse_Direction_Pair_Is_Allowed()
    {
        // The unique index is on (Source, Target) ordered tuple — (A, B) and (B, A) are
        // different rows from the DB's perspective. L2/L3 dedup via GetLinkedPeerDocumentIdsAsync
        // is what prevents the reverse direction from being AI-created; manual users can still
        // create both directions if they explicitly want.
        var a = _guidGenerator.Create();
        var b = _guidGenerator.Create();

        await WithTenantUowAsync(async () =>
        {
            await _relationRepository.InsertAsync(BuildRelation(a, b), autoSave: true);
            await _relationRepository.InsertAsync(BuildRelation(b, a), autoSave: true);
        });

        // No exception.
    }

    [Fact]
    public async Task DeleteAsync_Soft_Deletes_DocumentRelation()
    {
        // R2 contract: DocumentRelation : FullAuditedAggregateRoot implements ISoftDelete →
        // _relationRepository.DeleteAsync should mark IsDeleted=true rather than physically
        // removing the row. L2/L3's GetLinkedPeerDocumentIdsAsync(includeDismissed: true)
        // depends on the soft-deleted row staying in the table.
        var sourceId = _guidGenerator.Create();
        var targetId = _guidGenerator.Create();
        Guid firstId = default;

        await WithTenantUowAsync(async () =>
        {
            var first = BuildRelation(sourceId, targetId);
            firstId = first.Id;
            await _relationRepository.InsertAsync(first, autoSave: true);
        });

        await WithTenantUowAsync(async () =>
        {
            await _relationRepository.DeleteAsync(firstId, autoSave: true);
        });

        // Verify via raw SQL — bypasses ambient filters. The row must still exist
        // (soft-delete) with IsDeleted = 1.
        await WithUnitOfWorkAsync(async () =>
        {
            var ctxProvider = GetRequiredService<Volo.Abp.EntityFrameworkCore.IDbContextProvider<PaperbaseDbContext>>();
            var ctx = await ctxProvider.GetDbContextAsync();
            using var cmd = ctx.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = $"SELECT IsDeleted FROM PaperbaseDocumentRelations WHERE Id = '{firstId}' COLLATE NOCASE;";
            var raw = await cmd.ExecuteScalarAsync();
            raw.ShouldNotBeNull("row must remain in table after soft-delete (R2 contract)");
            Convert.ToInt32(raw).ShouldBe(1, "IsDeleted should flip to 1 after ABP soft-delete");
        });
    }

    [Fact]
    public async Task Re_Inserting_Pair_After_Soft_Delete_Is_Allowed()
    {
        // R2 dismissal flow end-to-end: user dismisses AI suggestion (DeleteAsync →
        // soft-delete) → manually re-creates the same pair → must succeed (filtered
        // index excludes IsDeleted=1 rows from the uniqueness constraint).
        var sourceId = _guidGenerator.Create();
        var targetId = _guidGenerator.Create();
        Guid firstId = default;

        await WithTenantUowAsync(async () =>
        {
            var first = BuildRelation(sourceId, targetId);
            firstId = first.Id;
            await _relationRepository.InsertAsync(first, autoSave: true);
        });

        await WithTenantUowAsync(async () =>
        {
            await _relationRepository.DeleteAsync(firstId, autoSave: true);
        });

        // Re-create — must NOT throw, because the soft-deleted row is excluded by
        // the filtered index (IsDeleted = 0 filter).
        await WithTenantUowAsync(async () =>
        {
            await _relationRepository.InsertAsync(
                BuildRelation(sourceId, targetId), autoSave: true);
        });
    }

    private DocumentRelation BuildRelation(Guid sourceId, Guid targetId)
    {
        return new DocumentRelation(
            _guidGenerator.Create(),
            tenantId: TestTenantId,
            sourceDocumentId: sourceId,
            targetDocumentId: targetId,
            description: "test relation",
            source: RelationSource.AiSuggested);
    }

    /// <summary>
    /// All test operations run as the TestTenantId — otherwise the ambient IMultiTenant filter
    /// (CurrentTenant.Id = null by default) hides the inserted rows from subsequent reads
    /// inside the same test, and GetAsync(id) throws EntityNotFoundException.
    /// </summary>
    private Task WithTenantUowAsync(Func<Task> action)
    {
        return WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TestTenantId))
            {
                await action();
            }
        });
    }
}

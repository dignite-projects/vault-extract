using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// EF Core implementation of <see cref="IDocumentPipelineRunRepository"/> (#216: split from the
/// Document child collection). The <c>IMultiTenant</c> global filter is applied automatically through
/// <see cref="EfCoreRepository{TDbContext,TEntity,TKey}.GetDbSetAsync"/>, so no handwritten TenantId
/// predicate is needed.
/// </summary>
public class EfCoreDocumentPipelineRunRepository
    : EfCoreRepository<ExtractDbContext, DocumentPipelineRun, Guid>, IDocumentPipelineRunRepository
{
    public EfCoreDocumentPipelineRunRepository(
        IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<DocumentPipelineRun?> FindLatestByDocumentAndCodeAsync(
        Guid documentId,
        string pipelineCode,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(r => r.DocumentId == documentId && r.PipelineCode == pipelineCode)
            .OrderByDescending(r => r.AttemptNumber)
            .FirstOrDefaultAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<Dictionary<string, DocumentPipelineRun>> GetLatestRunsByCodesAsync(
        Guid documentId,
        IReadOnlyCollection<string> pipelineCodes,
        CancellationToken cancellationToken = default)
    {
        if (pipelineCodes.Count == 0)
        {
            return new Dictionary<string, DocumentPipelineRun>();
        }

        var dbContext = await GetDbContextAsync();

        // Pick the run with the largest AttemptNumber per PipelineCode. EF Core 8+ translates
        // GroupBy(...).Select(g => g.OrderByDescending(...).First()) to SQL Server ROW_NUMBER() OVER
        // (PARTITION BY PipelineCode ORDER BY AttemptNumber DESC) WHERE rn = 1, so only one row per
        // code is returned. This does not rely on a "small row count + in-memory GroupBy" assumption.
        var latest = await dbContext.Set<DocumentPipelineRun>()
            .Where(r => r.DocumentId == documentId && pipelineCodes.Contains(r.PipelineCode))
            .GroupBy(r => r.PipelineCode)
            .Select(g => g.OrderByDescending(r => r.AttemptNumber).First())
            .ToListAsync(GetCancellationToken(cancellationToken));

        var result = latest.ToDictionary(r => r.PipelineCode);

        // Merge change-tracker entities that have not been flushed in this UoW. DeriveLifecycle runs
        // immediately after Manager UpdateAsync(run) (autoSave:false) / Insert calls, so the run's
        // post-change state may exist only in the tracker while the DB still has the old value, or
        // the new run has not been persisted yet. The GroupBy above chooses rows in the DB by the
        // persisted AttemptNumber and cannot see those unflushed changes. Explicitly merge Added /
        // Modified Local entries, keeping the largest AttemptNumber per PipelineCode, so the
        // responsibility for seeing the unflushed view stays in Infrastructure. Domain
        // DeriveLifecycleAsync therefore no longer needs the caller to pass the just-changed run
        // (#216 follow-up #1).
        foreach (var entry in dbContext.ChangeTracker.Entries<DocumentPipelineRun>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var run = entry.Entity;
            if (run.DocumentId != documentId || !pipelineCodes.Contains(run.PipelineCode))
            {
                continue;
            }

            // The unique index (DocumentId, PipelineCode, AttemptNumber) guarantees at most one row
            // for the same triple. In normal cases, the equality branch in >= only replaces the same
            // identity-map object reference with the tracker Modified entity, which is idempotent and
            // has no side effects. A new run that should take over is Added and necessarily has a
            // larger AttemptNumber. This matches the removed in-memory override semantics exactly.
            if (!result.TryGetValue(run.PipelineCode, out var existing)
                || run.AttemptNumber >= existing.AttemptNumber)
            {
                result[run.PipelineCode] = run;
            }
        }

        return result;
    }

    public virtual async Task<List<DocumentPipelineRun>> GetListByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(r => r.DocumentId == documentId)
            .OrderBy(r => r.PipelineCode)
            .ThenBy(r => r.AttemptNumber)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task InsertNewAttemptAsync(
        DocumentPipelineRun run,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // autoSave:true immediately calls SaveChanges, so key collisions throw DbUpdateException
            // here instead of being delayed until the outer commit.
            await InsertAsync(run, autoSave: true, GetCancellationToken(cancellationToken));
        }
        catch (DbUpdateException ex) when (IsAttemptNumberCollision(ex, run))
        {
            // #239: keep unique-constraint collision recognition in the persistence layer and catch
            // the provider-agnostic DbUpdateException type. Do not sniff messages or SQL Server error
            // codes, so behavior stays consistent across SQL Server / PostgreSQL / MySQL. The only
            // realistic cause is concurrent retries for the same Failed pipeline: the winner already
            // inserted the next AttemptNumber and moved the run to Pending; the loser collides and is
            // translated to RetryInProgress ("an attempt is already in progress"), matching the
            // concurrency guard semantics in EnsureRetryableAsync.
            throw new BusinessException(ExtractErrorCodes.Pipeline.RetryInProgress, innerException: ex)
                .WithData("PipelineCode", run.PipelineCode)
                .WithData("DocumentId", run.DocumentId);
        }
    }

    /// <summary>
    /// Determines whether this <see cref="DbUpdateException"/> was triggered by inserting
    /// <paramref name="run"/>. It uses EF Core <see cref="DbUpdateException.Entries"/> (provider
    /// agnostic) to locate the failed entity and does not depend on any database error code / text.
    /// The only constraint this insert can realistically violate is the
    /// <c>(DocumentId, PipelineCode, AttemptNumber)</c> unique index: the DocumentId FK must exist and
    /// non-null columns are guaranteed by the domain layer. A match is therefore treated as an
    /// AttemptNumber collision.
    /// </summary>
    protected virtual bool IsAttemptNumberCollision(DbUpdateException ex, DocumentPipelineRun run)
    {
        return ex.Entries.Any(e => ReferenceEquals(e.Entity, run));
    }
}

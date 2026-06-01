using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// <see cref="IDocumentPipelineRunRepository"/> 的 EF Core 实现（#216：拆于 Document 子集合）。
/// <c>IMultiTenant</c> 全局过滤器经 <see cref="EfCoreRepository{TDbContext,TEntity,TKey}.GetDbSetAsync"/>
/// 自动施加，无需手写 TenantId 谓词。
/// </summary>
public class EfCoreDocumentPipelineRunRepository
    : EfCoreRepository<PaperbaseDbContext, DocumentPipelineRun, Guid>, IDocumentPipelineRunRepository
{
    public EfCoreDocumentPipelineRunRepository(
        IDbContextProvider<PaperbaseDbContext> dbContextProvider)
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

        // 按 PipelineCode 取最大 AttemptNumber 的 run。EF Core 8+ 把
        // GroupBy(...).Select(g => g.OrderByDescending(...).First()) 翻译为 SQL Server ROW_NUMBER() OVER
        // (PARTITION BY PipelineCode ORDER BY AttemptNumber DESC) WHERE rn = 1，
        // 只回传"每 code 一行"——不靠"少量行 + 内存 GroupBy"的假设。
        var latest = await dbContext.Set<DocumentPipelineRun>()
            .Where(r => r.DocumentId == documentId && pipelineCodes.Contains(r.PipelineCode))
            .GroupBy(r => r.PipelineCode)
            .Select(g => g.OrderByDescending(r => r.AttemptNumber).First())
            .ToListAsync(GetCancellationToken(cancellationToken));

        var result = latest.ToDictionary(r => r.PipelineCode);

        // 合并本 UoW 内尚未 flush 的 change-tracker 实体。DeriveLifecycle 紧跟 Manager 的
        // UpdateAsync(run)（autoSave:false）/ Insert 调用——run 的 post-change 状态此刻可能只在
        // tracker、DB 仍是旧值（或新 run 尚未落库）。上面的 GroupBy 在 DB 端按已持久化的 AttemptNumber
        // 选行，看不到这些未 flush 修改。显式合并 Added/Modified 的 Local entries（取每个 PipelineCode
        // 下 AttemptNumber 最大者），把"看到未 flush 视图"的责任收敛在 Infrastructure 层——Domain 的
        // DeriveLifecycleAsync 因此不再需要调用方传入"刚改动的 run"（#216 follow-up #1）。
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

            // unique 索引 (DocumentId, PipelineCode, AttemptNumber) 保证同三元组至多一行：>= 的"相等"分支
            // 正常态下只会用 tracker 的 Modified 实体覆盖 identity-map 下的同一对象引用（幂等无副作用）；
            // 真正需要接管的新 run 走 Added 且 AttemptNumber 必然更大。与被删除的旧 in-memory override 语义逐字一致。
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

    public virtual async Task DetachAsync(
        DocumentPipelineRun entity,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextAsync();
        dbContext.Entry(entity).State = EntityState.Detached;
    }
}

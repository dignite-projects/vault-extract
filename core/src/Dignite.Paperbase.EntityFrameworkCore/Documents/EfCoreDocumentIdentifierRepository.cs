using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Issue #115 L1: <see cref="IDocumentIdentifierRepository"/> 的 EF Core 实现。
/// 所有路径（LINQ 查询 + <see cref="ExecuteDeleteAsync"/> 批量删除）均依赖 ABP <c>IMultiTenant</c>
/// query filter 自动注入 <c>TenantId</c> 谓词保证多租户隔离；显式 <c>Where(x =&gt; x.TenantId == ...)</c>
/// 不再重复添加。本路径不接 LLM 工具调用，不需要 doc-chat 反例 C 那种"显式断言 + 静态描述 + Take(N)"
/// 的工具体三件套——L2 Pipeline 跑在后台 worker，无 prompt-injection 攻击面。
/// </summary>
public class EfCoreDocumentIdentifierRepository
    : EfCoreRepository<PaperbaseDbContext, DocumentIdentifier, Guid>, IDocumentIdentifierRepository
{
    public EfCoreDocumentIdentifierRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<List<Guid>> FindDocumentIdsAsync(
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(i => i.IdentifierType == identifierType && i.IdentifierValue == identifierValue)
            .Select(i => i.DocumentId)
            .Distinct()
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<DocumentIdentifier>> GetListByDocumentIdAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(i => i.DocumentId == documentId)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<bool> ExistsAsync(
        Guid documentId,
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AnyAsync(
                i => i.DocumentId == documentId
                     && i.IdentifierType == identifierType
                     && i.IdentifierValue == identifierValue,
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task RemoveByDocumentIdAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        await dbSet
            .Where(i => i.DocumentId == documentId)
            .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
    }
}

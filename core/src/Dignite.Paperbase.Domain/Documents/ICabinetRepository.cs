using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents;

public interface ICabinetRepository : IRepository<Cabinet, Guid>
{
    /// <summary>
    /// 拿当前 ambient 租户层的全部文件柜（DisplayName ASC）。
    /// ABP <c>IMultiTenant</c> filter 按 <c>CurrentTenant.Id</c> 自动隔离单层——Host 文档管理者看 Host 柜，
    /// 租户 admin 看自己租户柜，不跨层 union。柜数量少，不分页。
    /// </summary>
    Task<List<Cabinet>> GetListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前层按柜名精确查找（用于 CRUD 判重）。只查活跃柜——Cabinet 无回收站，软删柜名可被新柜复用
    /// （唯一索引带 <c>IsDeleted = 0</c> 过滤）。
    /// </summary>
    Task<Cabinet?> FindByDisplayNameAsync(string displayName, CancellationToken cancellationToken = default);
}

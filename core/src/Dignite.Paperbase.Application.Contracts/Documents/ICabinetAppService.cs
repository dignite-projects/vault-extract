using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文件柜管理（#194）。两层独立单层（参照 <see cref="IDocumentTypeAppService"/>）：
/// <para>
/// <see cref="GetListAsync"/> 返回当前层全部柜（Host admin → Host 柜；租户 admin → 自己租户柜，不跨层 union）；
/// Create / Update / Delete 只作用于当前层（TenantId == CurrentTenant.Id）。
/// </para>
/// <para>
/// 与 <see cref="IDocumentTypeAppService"/> 的区别——<b>不做回收站</b>（柜删错重建即可，无级联字段定义）；
/// <b>删除不阻止 InUse</b>（柜正交于 pipeline），但删柜会<b>原子清空</b>该柜全部文档的 CabinetId 让它们回退
/// "未归类"，不留悬空引用，不影响分类 / 抽取。
/// </para>
/// </summary>
public interface ICabinetAppService : IApplicationService
{
    Task<List<CabinetDto>> GetListAsync();

    Task<CabinetDto> CreateAsync(CreateCabinetDto input);

    Task<CabinetDto> UpdateAsync(Guid id, UpdateCabinetDto input);

    Task DeleteAsync(Guid id);
}

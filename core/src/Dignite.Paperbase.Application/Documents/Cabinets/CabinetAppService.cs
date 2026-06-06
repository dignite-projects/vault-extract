using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 文件柜管理（#194）。匹配当前层、不跨层 union。权限 fail-closed（方法级 Cabinets.* 断言）。
/// </summary>
[Authorize(PaperbasePermissions.Cabinets.Default)]
public class CabinetAppService : PaperbaseAppService, ICabinetAppService
{
    private readonly ICabinetRepository _repository;
    private readonly IDocumentRepository _documentRepository;

    public CabinetAppService(
        ICabinetRepository repository,
        IDocumentRepository documentRepository)
    {
        _repository = repository;
        _documentRepository = documentRepository;
    }

    public virtual async Task<List<CabinetDto>> GetListAsync()
    {
        // 当前层全部柜（ambient IMultiTenant filter 按 CurrentTenant.Id 隔离单层）。
        var list = await _repository.GetListAsync();
        return ObjectMapper.Map<List<Cabinet>, List<CabinetDto>>(list);
    }

    [Authorize(PaperbasePermissions.Cabinets.Create)]
    public virtual async Task<CabinetDto> CreateAsync(CreateCabinetDto input)
    {
        await EnsureNameAvailableAsync(input.Name);

        var entity = new Cabinet(GuidGenerator.Create(), CurrentTenant.Id, input.Name, input.Description);
        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(PaperbasePermissions.Cabinets.Update)]
    public virtual async Task<CabinetDto> UpdateAsync(Guid id, UpdateCabinetDto input)
    {
        var entity = await _repository.GetAsync(id);

        // 跨层防御：只能改自己所在层。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // Name 是可改的唯一键——仅改名时判重（同名不变则跳过，避免误判自身冲突）。
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await EnsureNameAvailableAsync(input.Name);
        }

        entity.Update(input.Name, input.Description);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(PaperbasePermissions.Cabinets.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // 删柜前原子清空该柜文档的 CabinetId（真正 unfile）——否则文档悬空指向已删柜（stale ID、重建同名柜无法归位）。
        // 不阻止删除（区别于 DocumentType 的 InUse 保护）；只清活跃文档（软删文档的 stale CabinetId 无害，前端 map 不到即「未归类」）。
        // 单柜文档极多时可换 ExecuteUpdateAsync。
        var orphans = await _documentRepository.GetListAsync(
            d => d.TenantId == CurrentTenant.Id && d.CabinetId == entity.Id);
        if (orphans.Count > 0)
        {
            foreach (var doc in orphans)
            {
                doc.UnassignCabinet();
            }
            await _documentRepository.UpdateManyAsync(orphans, autoSave: true);
        }

        await _repository.DeleteAsync(entity);
    }

    /// <summary>
    /// 当前层柜名判重——只查活跃柜（不含软删除）。Cabinet 不做回收站，软删即遗忘，其名字可被新柜复用
    /// （唯一索引 <c>(TenantId, Name)</c> 带 <c>IsDeleted = 0</c> 过滤，软删柜不参与活跃约束）。
    /// </summary>
    protected virtual async Task EnsureNameAvailableAsync(string name)
    {
        var existing = await _repository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.Cabinet.NameAlreadyExists)
                .WithData("Name", name);
        }
    }
}

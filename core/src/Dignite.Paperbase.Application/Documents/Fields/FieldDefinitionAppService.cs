using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents.Fields;

[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class FieldDefinitionAppService : PaperbaseAppService, IFieldDefinitionAppService
{
    private readonly IFieldDefinitionRepository _repository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDocumentRepository _documentRepository;

    public FieldDefinitionAppService(
        IFieldDefinitionRepository repository,
        IDocumentTypeRepository documentTypeRepository,
        IDocumentRepository documentRepository)
    {
        _repository = repository;
        _documentTypeRepository = documentTypeRepository;
        _documentRepository = documentRepository;
    }

    public virtual async Task<List<FieldDefinitionDto>> GetListAsync(GetFieldDefinitionListInput input)
    {
        // 仅当前租户层字段（CLAUDE.md "两层 mutually exclusive 不混"）——租户隔离由 ABP IMultiTenant 全局过滤器施加。
        // 按不可变 DocumentTypeId 精确匹配单层（#207）；类型不存在时自然返回空集。
        if (input.OnlyDeleted)
        {
            // 回收站视图：穿透 soft-delete 过滤，仅取 IsDeleted，按删除时间倒序。
            using (DataFilter.Disable<ISoftDelete>())
            {
                var queryable = await _repository.GetQueryableAsync();
                var deleted = await AsyncExecuter.ToListAsync(
                    queryable
                        .Where(f =>
                            f.DocumentTypeId == input.DocumentTypeId &&
                            f.IsDeleted)
                        .OrderByDescending(f => f.DeletionTime));
                return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(deleted);
            }
        }

        var list = await _repository.GetListAsync(input.DocumentTypeId);
        return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(list);
    }

    public virtual async Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input)
    {
        // 父类型必须存在于当前层（#207 FieldDefinition.DocumentTypeId FK RESTRICT；IMultiTenant + ISoftDelete 过滤保证跨层/已删返回 null）。
        var type = await _documentTypeRepository.FindAsync(input.DocumentTypeId);
        if (type == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), input.DocumentTypeId);
        }

        // 关闭 ISoftDelete 过滤——同 (TenantId, DocumentTypeId, Name) 即使软删除态也算占用，避免恢复时与新记录冲突。
        FieldDefinition? existing;
        using (DataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByNameAsync(input.DocumentTypeId, input.Name);
        }
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.AlreadyExists)
                .WithData("DocumentTypeCode", type.TypeCode)
                .WithData("Name", input.Name);
        }

        var entity = new FieldDefinition(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.DocumentTypeId,
            input.Name,
            input.DisplayName,
            input.Prompt,
            input.DataType,
            input.DisplayOrder,
            input.IsRequired);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    public virtual async Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input)
    {
        var entity = await _repository.GetAsync(id);

        // 跨层防御：只能改自己所在层。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }

        // 重命名解锁（#207）：仅在 Name 变化时判重（同层同类型唯一，含软删占用）。
        if (!string.Equals(input.Name, entity.Name, StringComparison.Ordinal))
        {
            FieldDefinition? conflict;
            using (DataFilter.Disable<ISoftDelete>())
            {
                conflict = await _repository.FindByNameAsync(entity.DocumentTypeId, input.Name);
            }
            if (conflict != null)
            {
                // 仅错误路径解析 TypeCode 供人读消息（happy path 不查）。
                throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.AlreadyExists)
                    .WithData("DocumentTypeCode", await ResolveTypeCodeAsync(entity.DocumentTypeId) ?? string.Empty)
                    .WithData("Name", input.Name);
            }
        }

        // DataType 变更守卫（#207）：已有抽取值的字段禁止改 DataType——历史值落在旧 typed 列，按新类型查会静默漏掉。
        // 需换类型请新建字段。
        if (input.DataType != entity.DataType
            && await _documentRepository.AnyExtractedFieldValueAsync(entity.Id))
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.DataTypeChangeNotAllowed)
                .WithData("Name", entity.Name);
        }

        entity.Update(input.Name, input.DisplayName, input.Prompt, input.DataType, input.DisplayOrder, input.IsRequired);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }
        await _repository.DeleteAsync(entity);
    }

    public virtual async Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(FieldDefinition), id);
            }

            // 已在 Disable<ISoftDelete> 作用域内——可解析到（即便已软删的）父类型 TypeCode 用于错误信息 / DTO。
            var parentType = await _documentTypeRepository.FindAsync(entity.DocumentTypeId);
            var documentTypeCode = parentType?.TypeCode;

            // 幂等：未删除直接返回。
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
            }

            // 父类型必须存在且活跃——严格单层匹配（与 FieldExtractionEventHandler 一致）。
            // 父类型仍处于已删除态时，应走 IDocumentTypeAppService.RestoreAsync 的级联路径。
            if (parentType == null || parentType.IsDeleted)
            {
                throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.ParentTypeMissing)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("Name", entity.Name);
            }

            // 同名活跃字段冲突——CreateAsync 判重应当已防住，防御性补一道。
            var queryable = await _repository.GetQueryableAsync();
            var nameConflict = await AsyncExecuter.AnyAsync(
                queryable.Where(f =>
                    f.TenantId == entity.TenantId &&
                    f.DocumentTypeId == entity.DocumentTypeId &&
                    f.Name == entity.Name &&
                    !f.IsDeleted));
            if (nameConflict)
            {
                throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.RestoreConflict)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("Name", entity.Name);
            }

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
        }
    }

    /// <summary>解析字段所属类型的 TypeCode（穿透 soft-delete），仅用于人读错误消息（#207：API 出口已是 DocumentTypeId）。</summary>
    protected virtual async Task<string?> ResolveTypeCodeAsync(Guid documentTypeId)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var type = await _documentTypeRepository.FindAsync(documentTypeId);
            return type?.TypeCode;
        }
    }
}

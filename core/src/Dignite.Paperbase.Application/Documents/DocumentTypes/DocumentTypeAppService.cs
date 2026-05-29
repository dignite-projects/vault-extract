using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents.DocumentTypes;

[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class DocumentTypeAppService : PaperbaseAppService, IDocumentTypeAppService
{
    private readonly IDocumentTypeRepository _repository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;

    public DocumentTypeAppService(
        IDocumentTypeRepository repository,
        IDocumentRepository documentRepository,
        IFieldDefinitionRepository fieldDefinitionRepository)
    {
        _repository = repository;
        _documentRepository = documentRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
    }

    public virtual async Task<List<DocumentTypeDto>> GetVisibleAsync()
    {
        // 不做 Host ∪ Tenant union；租户隔离由 ambient IMultiTenant 过滤器施加。
        // Priority DESC + TypeCode ASC 排序在内存中保持。
        var list = (await _repository.GetListAsync())
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.TypeCode)
            .ToList();
        return ObjectMapper.Map<List<DocumentType>, List<DocumentTypeDto>>(list);
    }

    public virtual async Task<List<DocumentTypeDto>> GetDeletedAsync()
    {
        // 仅关闭 ISoftDelete 看到已删除行；租户隔离仍由 ambient IMultiTenant 过滤器施加，不跨层。
        using (DataFilter.Disable<ISoftDelete>())
        {
            var queryable = await _repository.GetQueryableAsync();
            var list = await AsyncExecuter.ToListAsync(
                queryable
                    .Where(t => t.IsDeleted)
                    .OrderByDescending(t => t.DeletionTime));
            return ObjectMapper.Map<List<DocumentType>, List<DocumentTypeDto>>(list);
        }
    }

    public virtual async Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input)
    {
        // 严格单层判重——TypeCode 是 per-layer 命名空间，Host 与 tenant 各自独立，
        // 跨层同 TypeCode 是合法的两行（由 TenantId 区分）。下游消费方按
        // (TenantId, DocumentTypeCode) 元组消费。
        // 关闭 ISoftDelete 过滤，让软删除记录也参与判重——否则"删除→重建同 TypeCode→
        // 恢复旧记录"路径会触发唯一索引冲突或两行同 (TenantId, TypeCode) 活跃。
        DocumentType? existing;
        using (DataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByTypeCodeAsync(input.TypeCode);
        }
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.CodeAlreadyExists)
                .WithData("TypeCode", input.TypeCode);
        }

        var entity = new DocumentType(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.TypeCode,
            input.DisplayName,
            input.ConfidenceThreshold,
            input.Priority);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    public virtual async Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input)
    {
        var entity = await _repository.GetAsync(id);

        // 跨层防御：只能改自己所在层。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        // 重命名解锁（#207）：仅在 TypeCode 变化时判重（同层 (TenantId, TypeCode) 唯一；含软删占用，避免恢复冲突）。
        // 内部关联（Document / FieldDefinition / ExportTemplate）已用本类型的不可变 Id，rename 不级联这些表。
        if (!string.Equals(input.TypeCode, entity.TypeCode, StringComparison.Ordinal))
        {
            DocumentType? conflict;
            using (DataFilter.Disable<ISoftDelete>())
            {
                conflict = await _repository.FindByTypeCodeAsync(input.TypeCode);
            }
            if (conflict != null)
            {
                throw new BusinessException(PaperbaseErrorCodes.DocumentType.CodeAlreadyExists)
                    .WithData("TypeCode", input.TypeCode);
            }
        }

        entity.Update(input.TypeCode, input.DisplayName, input.ConfidenceThreshold, input.Priority);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);

        // Fail-closed：仍有文档引用此类型时阻止删除——强制租户先 reclassify 这些文档。按内部 DocumentTypeId 判定（#207）。
        // 租户隔离由 ambient IMultiTenant 过滤器施加（GetAsync 与 document 查询都自动按当前层过滤）。
        // 注：软删除走 UPDATE 不触发 FK；此应用层闸门 + DocumentType→Document 无 FK，故这里显式拦在用类型。
        var documentQueryable = await _documentRepository.GetQueryableAsync();
        var inUse = await AsyncExecuter.AnyAsync(
            documentQueryable.Where(d => d.DocumentTypeId == entity.Id));
        if (inUse)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.InUse)
                .WithData("TypeCode", entity.TypeCode);
        }

        // 级联软删除：同类型（DocumentTypeId）下的 FieldDefinition 随 DocumentType 一并下线，
        // 否则会留下孤儿字段定义且未来重建同 TypeCode 时无法复用同名字段。
        var fields = await _fieldDefinitionRepository.GetListAsync(entity.Id);
        if (fields.Count > 0)
        {
            await _fieldDefinitionRepository.DeleteManyAsync(fields);
        }

        await _repository.DeleteAsync(entity);
    }

    public virtual async Task<DocumentTypeDto> RestoreAsync(Guid id)
    {
        // 整段恢复在禁用 ISoftDelete 的 scope 里执行：查询能看到已删除行，写入能把 IsDeleted=false。
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(DocumentType), id);
            }

            // 幂等：未删除的直接返回当前状态。
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
            }

            // 防御：同 (TenantId, TypeCode) 已有活跃行——CreateAsync 的判重应当已防住，
            // 但极端情况下（手工 DB / seed bypass）仍可能发生，避免唯一索引冲突。
            var typeQueryable = await _repository.GetQueryableAsync();
            var typeConflict = await AsyncExecuter.AnyAsync(
                typeQueryable.Where(t =>
                    t.TenantId == entity.TenantId &&
                    t.TypeCode == entity.TypeCode &&
                    !t.IsDeleted));
            if (typeConflict)
            {
                throw new BusinessException(PaperbaseErrorCodes.DocumentType.RestoreConflict)
                    .WithData("TypeCode", entity.TypeCode);
            }

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            // 级联恢复：同 (TenantId, TypeCode) 下软删除的 FieldDefinition 一并恢复。
            // 与单字段 RestoreAsync 不同——这里跳过冲突字段（记录 warning），不中断整体恢复。
            var fieldQueryable = await _fieldDefinitionRepository.GetQueryableAsync();
            var deletedFields = await AsyncExecuter.ToListAsync(
                fieldQueryable.Where(f =>
                    f.TenantId == entity.TenantId &&
                    f.DocumentTypeId == entity.Id &&
                    f.IsDeleted));

            foreach (var field in deletedFields)
            {
                var nameConflict = await AsyncExecuter.AnyAsync(
                    fieldQueryable.Where(f =>
                        f.TenantId == entity.TenantId &&
                        f.DocumentTypeId == entity.Id &&
                        f.Name == field.Name &&
                        !f.IsDeleted));
                if (nameConflict)
                {
                    Logger.LogWarning(
                        "Skip cascade restore of FieldDefinition {FieldId} (Name={Name}) under DocumentType {TypeCode}: an active field with the same name already exists.",
                        field.Id, field.Name, entity.TypeCode);
                    continue;
                }

                field.IsDeleted = false;
                field.DeletionTime = null;
                field.DeleterId = null;
                await _fieldDefinitionRepository.UpdateAsync(field);
            }

            return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
        }
    }
}

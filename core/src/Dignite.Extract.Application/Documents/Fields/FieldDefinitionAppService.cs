using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Extract.Documents.DocumentTypes;
using Dignite.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;

namespace Dignite.Extract.Documents.Fields;

// Authorization is declared per method (#223): reading field schema (active GetListAsync) is decoupled from schema management.
// Therefore there is no class-level [Authorize]; each method explicitly declares its own permission gate,
// using the same programmatic pattern as DocumentAppService.
public class FieldDefinitionAppService : ExtractAppService, IFieldDefinitionAppService
{
    private readonly IFieldDefinitionRepository _repository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly FieldDefinitionManager _fieldDefinitionManager;

    public FieldDefinitionAppService(
        IFieldDefinitionRepository repository,
        IDocumentTypeRepository documentTypeRepository,
        IDocumentRepository documentRepository,
        FieldDefinitionManager fieldDefinitionManager)
    {
        _repository = repository;
        _documentTypeRepository = documentTypeRepository;
        _documentRepository = documentRepository;
        _fieldDefinitionManager = fieldDefinitionManager;
    }

    public virtual async Task<List<FieldDefinitionDto>> GetListAsync(GetFieldDefinitionListInput input)
    {
        // Current tenant layer only (CLAUDE.md "two layers are mutually exclusive, no mixing").
        // Tenant isolation is enforced by the ABP IMultiTenant global filter.
        // When DocumentTypeId is specified, match exactly one type by immutable Id (#207); missing type naturally returns an empty set.
        // Empty = all field definitions in the current layer, the batch path used by MCP list_document_types and similar callers to fetch once and avoid per-type N+1.
        if (input.OnlyDeleted)
        {
            // Trash view is consumed only by schema management screens, so keep the admin gate (#223).
            await CheckPolicyAsync(ExtractPermissions.FieldDefinitions.Default);

            // Trash view: traverse soft-delete filter, take only IsDeleted, ordered by deletion time descending.
            using (DataFilter.Disable<ISoftDelete>())
            {
                var queryable = await _repository.GetQueryableAsync();
                var deletedQuery = queryable.Where(f => f.IsDeleted);
                if (input.DocumentTypeId != null)
                {
                    deletedQuery = deletedQuery.Where(f => f.DocumentTypeId == input.DocumentTypeId);
                }
                var deleted = await AsyncExecuter.ToListAsync(
                    deletedQuery.OrderByDescending(f => f.DeletionTime));
                return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(deleted);
            }
        }

        // Active field schema reads are decoupled from schema management (#223): document operators (Documents.Default) need field definitions
        // to drive dynamic field columns / detail field editing / export column selection; field admins (FieldDefinitions.Default)
        // need to read their own management list. Either is enough: fail-closed OR assertion.
        // Batch queries (DocumentTypeId empty) and type-scoped queries use the same permission gate and do not widen visibility;
        // enumerating per type could already obtain the same set.
        if (!await AuthorizationService.IsGrantedAsync(ExtractPermissions.Documents.Default) &&
            !await AuthorizationService.IsGrantedAsync(ExtractPermissions.FieldDefinitions.Default))
        {
            throw new AbpAuthorizationException();
        }

        if (input.DocumentTypeId == null)
        {
            // Batch path: query all active fields in the current layer once, with IMultiTenant + ISoftDelete filters still applied.
            // Stable-sort by DocumentTypeId then DisplayOrder; callers group in memory.
            var queryable = await _repository.GetQueryableAsync();
            var all = await AsyncExecuter.ToListAsync(
                queryable
                    .OrderBy(f => f.DocumentTypeId)
                    .ThenBy(f => f.DisplayOrder));
            return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(all);
        }

        var list = await _repository.GetListAsync(input.DocumentTypeId.Value);
        return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(list);
    }

    [Authorize(ExtractPermissions.FieldDefinitions.Create)]
    public virtual async Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input)
    {
        // Parent type must exist in the current layer (#207 FieldDefinition.DocumentTypeId FK RESTRICT).
        // IMultiTenant + ISoftDelete filters ensure cross-layer / deleted types return null.
        var type = await _documentTypeRepository.FindAsync(input.DocumentTypeId);
        if (type == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), input.DocumentTypeId);
        }

        // Soft-delete-aware duplicate check owned by the domain service (#304): the same (TenantId, DocumentTypeId, Name)
        // counts as occupied even when soft-deleted, avoiding conflicts with new records on restore.
        await _fieldDefinitionManager.CheckNameAvailableAsync(input.DocumentTypeId, input.Name);

        var entity = new FieldDefinition(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.DocumentTypeId,
            input.Name,
            input.DisplayName,
            input.Prompt,
            input.DataType,
            input.DisplayOrder,
            input.IsRequired,
            input.AllowMultiple,
            input.IsUniqueKey);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    [Authorize(ExtractPermissions.FieldDefinitions.Update)]
    public virtual async Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input)
    {
        var entity = await _repository.GetAsync(id);

        // Cross-layer defense: callers may modify only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }

        // Rename unlock (#207): run the domain duplicate check only when Name changes. Same layer + same type is unique,
        // including soft-deleted occupancy. The manager resolves the owning TypeCode for the error message only on conflict.
        if (!string.Equals(input.Name, entity.Name, StringComparison.Ordinal))
        {
            await _fieldDefinitionManager.CheckNameAvailableAsync(entity.DocumentTypeId, input.Name);
        }

        // Two "forbid when extracted values exist" guards share the same fact: whether this field has any value rows.
        // Query once only when a relevant change needs a decision:
        // - DataType change (#207): historical values live in the old typed column and silently disappear when queried as the new type.
        // - Multi-value narrowing multi -> single (#212): Order>0 rows become orphans; exports render only Order 0, silently dropping extra values while storage rows remain.
        //   single -> multi is lossless broadening because existing single-value rows become a one-element list, so it is not guarded here.
        var dataTypeChanged = input.DataType != entity.DataType;
        var multiValueNarrowed = entity.AllowMultiple && !input.AllowMultiple;
        if (dataTypeChanged || multiValueNarrowed)
        {
            var hasValues = await _documentRepository.AnyExtractedFieldValueAsync(entity.Id);
            if (dataTypeChanged && hasValues)
            {
                throw new BusinessException(ExtractErrorCodes.FieldDefinition.DataTypeChangeNotAllowed)
                    .WithData("Name", entity.Name);
            }
            if (multiValueNarrowed && hasValues)
            {
                throw new BusinessException(ExtractErrorCodes.FieldDefinition.MultiValueChangeNotAllowed)
                    .WithData("Name", entity.Name);
            }
        }

        entity.Update(input.Name, input.DisplayName, input.Prompt, input.DataType, input.DisplayOrder, input.IsRequired, input.AllowMultiple, input.IsUniqueKey);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    [Authorize(ExtractPermissions.FieldDefinitions.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }
        await _repository.DeleteAsync(entity);
    }

    [Authorize(ExtractPermissions.FieldDefinitions.Delete)]
    public virtual async Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(FieldDefinition), id);
            }

            // Already inside Disable<ISoftDelete>, so the parent type TypeCode can be resolved even if soft-deleted for error messages / DTO.
            var parentType = await _documentTypeRepository.FindAsync(entity.DocumentTypeId);
            var documentTypeCode = parentType?.TypeCode;

            // Idempotent: return directly when not deleted.
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
            }

            // Parent type must exist and be active, with strict single-layer matching (consistent with FieldExtractionEventHandler).
            // If the parent type is still deleted, use the cascading path in IDocumentTypeAppService.RestoreAsync instead.
            if (parentType == null || parentType.IsDeleted)
            {
                throw new BusinessException(ExtractErrorCodes.FieldDefinition.ParentTypeMissing)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("Name", entity.Name);
            }

            // Active field with the same name conflicts. CreateAsync duplicate checks should already prevent this; the domain
            // service keeps a defensive guard and throws RestoreConflict (#304).
            await _fieldDefinitionManager.CheckRestorableAsync(entity);

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
        }
    }
}

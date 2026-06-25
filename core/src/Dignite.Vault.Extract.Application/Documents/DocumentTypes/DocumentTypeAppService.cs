using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

// Authorization is declared per method (#223): schema reads (GetVisibleAsync) are decoupled from schema management.
// Therefore there is no class-level [Authorize]; each method explicitly declares its own permission gate,
// using the same programmatic pattern as DocumentAppService.
public class DocumentTypeAppService : ExtractAppService, IDocumentTypeAppService
{
    private readonly IDocumentTypeRepository _repository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly DocumentTypeManager _documentTypeManager;
    private readonly Fields.FieldDefinitionManager _fieldDefinitionManager;

    public DocumentTypeAppService(
        IDocumentTypeRepository repository,
        IDocumentRepository documentRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        DocumentTypeManager documentTypeManager,
        Fields.FieldDefinitionManager fieldDefinitionManager)
    {
        _repository = repository;
        _documentRepository = documentRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _documentTypeManager = documentTypeManager;
        _fieldDefinitionManager = fieldDefinitionManager;
    }

    public virtual async Task<List<DocumentTypeDto>> GetVisibleAsync()
    {
        // Schema reads are decoupled from schema management (#223): document operators (Documents.Default) need to read types
        // for type filters / classification assignment / dynamic field columns, while schema admins (DocumentTypes.Default)
        // need to read their own management list. Either permission is enough: fail-closed OR assertion.
        // Programmatic because [Authorize] does not trigger on reflection / non-HTTP paths.
        if (!await AuthorizationService.IsGrantedAsync(ExtractPermissions.Documents.Default) &&
            !await AuthorizationService.IsGrantedAsync(ExtractPermissions.DocumentTypes.Default))
        {
            throw new AbpAuthorizationException();
        }

        // Do not union Host and Tenant. Tenant isolation is enforced by the ambient IMultiTenant filter.
        // Keep Priority DESC + TypeCode ASC ordering in memory.
        var list = (await _repository.GetListAsync())
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.TypeCode)
            .ToList();
        return ObjectMapper.Map<List<DocumentType>, List<DocumentTypeDto>>(list);
    }

    [Authorize(ExtractPermissions.DocumentTypes.Default)]
    public virtual async Task<List<DocumentTypeDto>> GetDeletedAsync()
    {
        // Trash view is consumed only by schema management screens, so keep the admin gate (#223).
        // Disable only ISoftDelete to see deleted rows; tenant isolation is still enforced by the ambient IMultiTenant filter and does not cross layers.
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

    [Authorize(ExtractPermissions.DocumentTypes.Create)]
    public virtual async Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input)
    {
        // Strict single-layer duplicate check, owned by the domain service (#304): TypeCode is a per-layer namespace;
        // Host and each tenant are independent, so the same TypeCode across layers is valid as two rows distinguished by
        // TenantId (downstream consumers use the (TenantId, DocumentTypeCode) tuple). The check is soft-delete-aware so the
        // "delete -> recreate same TypeCode -> restore old record" path cannot produce two active rows.
        await _documentTypeManager.CheckCodeAvailableAsync(input.TypeCode);

        var entity = new DocumentType(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.TypeCode,
            input.DisplayName,
            input.Description,
            input.ConfidenceThreshold,
            input.Priority);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    [Authorize(ExtractPermissions.DocumentTypes.Update)]
    public virtual async Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input)
    {
        var entity = await _repository.GetAsync(id);

        // Cross-layer defense: callers may modify only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        // Rename unlock (#207): run the domain duplicate check only when TypeCode changes. Same-layer (TenantId, TypeCode)
        // is unique; soft-deleted records occupy the name too, avoiding restore conflicts.
        // Internal associations (Document / FieldDefinition / ExportTemplate) already use this type's immutable Id, so rename does not cascade to those tables.
        if (!string.Equals(input.TypeCode, entity.TypeCode, StringComparison.Ordinal))
        {
            await _documentTypeManager.CheckCodeAvailableAsync(input.TypeCode);
        }

        entity.Update(input.TypeCode, input.DisplayName, input.Description, input.ConfidenceThreshold, input.Priority);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    [Authorize(ExtractPermissions.DocumentTypes.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);

        // Fail-closed: prevent deletion while documents still reference this type, forcing the tenant to reclassify those documents first.
        // Determined by internal DocumentTypeId (#207). Tenant isolation is enforced by the ambient IMultiTenant filter;
        // GetAsync and document queries are both automatically filtered to the current layer.
        // Note: soft delete is an UPDATE and does not trigger FKs. Since this application-layer gate has no DocumentType -> Document FK,
        // explicitly block deletion of in-use types here.
        var documentQueryable = await _documentRepository.GetQueryableAsync();
        var inUse = await AsyncExecuter.AnyAsync(
            documentQueryable.Where(d => d.DocumentTypeId == entity.Id));
        if (inUse)
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.InUse)
                .WithData("TypeCode", entity.TypeCode);
        }

        // Cascading soft delete: FieldDefinitions under the same DocumentTypeId go offline with DocumentType.
        // Otherwise orphan field definitions would remain, and a future recreation with the same TypeCode could not reuse the same field names.
        var fields = await _fieldDefinitionRepository.GetListAsync(entity.Id);
        if (fields.Count > 0)
        {
            await _fieldDefinitionRepository.DeleteManyAsync(fields);
        }

        await _repository.DeleteAsync(entity);
    }

    [Authorize(ExtractPermissions.DocumentTypes.Delete)]
    public virtual async Task<DocumentTypeDto> RestoreAsync(Guid id)
    {
        // The whole restore block runs with ISoftDelete disabled: queries can see deleted rows and writes can set IsDeleted=false.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(DocumentType), id);
            }

            // Idempotent: if not deleted, return the current state directly.
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
            }

            // Defense: an active row with the same (TenantId, TypeCode) already exists. CreateAsync duplicate checks should prevent this,
            // but extreme cases such as manual DB edits / seed bypass can still happen; the domain check rejects it (#304).
            await _documentTypeManager.CheckRestorableAsync(entity);

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            // Cascading restore: also restore soft-deleted FieldDefinitions under the same (TenantId, TypeCode).
            // Unlike single-field RestoreAsync, skip conflicting fields here and log a warning rather than interrupting the whole restore.
            var fieldQueryable = await _fieldDefinitionRepository.GetQueryableAsync();
            var deletedFields = await AsyncExecuter.ToListAsync(
                fieldQueryable.Where(f =>
                    f.TenantId == entity.TenantId &&
                    f.DocumentTypeId == entity.Id &&
                    f.IsDeleted));

            foreach (var field in deletedFields)
            {
                // Per-field duplicate check routes through the domain service (#304); cascade restore skips conflicts
                // (logs a warning) instead of aborting the whole type restore.
                var nameConflict = await _fieldDefinitionManager.HasActiveNameConflictAsync(entity.Id, field.Name);
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

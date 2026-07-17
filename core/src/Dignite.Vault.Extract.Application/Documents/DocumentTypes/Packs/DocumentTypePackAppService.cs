using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// Config import/export "pack" engine (#444). Serializes a <see cref="DocumentType"/> + its
/// <see cref="FieldDefinition"/>s to a portable declarative pack and applies a pack back idempotently.
/// <para>
/// Everything goes through the domain entities + managers (never a raw DbContext), so every invariant holds:
/// code/name layer-uniqueness (<see cref="DocumentTypeManager"/> / <see cref="FieldDefinitionManager"/>),
/// entity validation (name pattern, lengths, multi-value-only-for-text), and the data-safety guard that
/// forbids changing a field's data type or narrowing multi→single once extracted values exist.
/// </para>
/// <para>
/// Layer-aware: reads and writes only the caller's current layer (Host = <c>TenantId</c> null, tenant = its
/// GUID) via the ambient <c>IMultiTenant</c> filter + <c>CurrentTenant.Id</c>; there is no cross-layer
/// mixing. Identity is <c>TypeCode</c> / field <c>Name</c> (#207: a rename = a new type/field). Import is
/// atomic: it runs in the ambient application-service unit of work, and all pack versions are validated
/// before any write, so an unsupported version leaves nothing partially applied.
/// </para>
/// </summary>
public class DocumentTypePackAppService : VaultExtractAppService, IDocumentTypePackAppService
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentTypeManager _documentTypeManager;
    private readonly FieldDefinitionManager _fieldDefinitionManager;
    private readonly FieldSchemaPromptBudgetGuard _schemaPromptBudget;

    public DocumentTypePackAppService(
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        IDocumentRepository documentRepository,
        DocumentTypeManager documentTypeManager,
        FieldDefinitionManager fieldDefinitionManager,
        FieldSchemaPromptBudgetGuard schemaPromptBudget)
    {
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _documentRepository = documentRepository;
        _documentTypeManager = documentTypeManager;
        _fieldDefinitionManager = fieldDefinitionManager;
        _schemaPromptBudget = schemaPromptBudget;
    }

    [Authorize(VaultExtractPermissions.DocumentTypes.Default)]
    public virtual async Task<DocumentTypePackDto> ExportAsync(Guid id)
    {
        var type = await _documentTypeRepository.GetAsync(id);
        // Defense in depth on top of the IMultiTenant filter: never export another layer's type.
        if (type.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
        return MapToPack(type, fields);
    }

    [Authorize(VaultExtractPermissions.DocumentTypes.Default)]
    public virtual async Task<List<DocumentTypePackDto>> ExportAllAsync()
    {
        // The ambient IMultiTenant filter narrows this to the caller's layer.
        var types = await _documentTypeRepository.GetListAsync();
        var packs = new List<DocumentTypePackDto>(types.Count);
        foreach (var type in types.OrderBy(t => t.Priority).ThenBy(t => t.TypeCode))
        {
            var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
            packs.Add(MapToPack(type, fields));
        }

        return packs;
    }

    // Import always may create types + fields, so both Create permissions gate entry. The Update permissions
    // are asserted lazily, only on the branches that actually update an existing type / field (ImportPackAsync
    // / ImportFieldsAsync), so a CreateOnly import — or a first-time import of all-new types/fields — never
    // demands Update. This is not an LLM-influenced path, so [Authorize] fires normally.
    [Authorize(VaultExtractPermissions.DocumentTypes.Create)]
    [Authorize(VaultExtractPermissions.FieldDefinitions.Create)]
    public virtual async Task<DocumentTypePackImportResultDto> ImportAsync(ImportDocumentTypePacksInput input)
    {
        // Validate every pack version up front, before touching the store, so an unsupported version can
        // never leave earlier packs partially applied (the production UoW would roll back anyway, but the
        // test harness disables the transaction — pre-validation makes the guarantee unconditional).
        foreach (var pack in input.Packs)
        {
            if (pack.Version != DocumentTypePackConsts.CurrentVersion)
            {
                throw new BusinessException(VaultExtractErrorCodes.DocumentTypePack.UnsupportedVersion)
                    .WithData("TypeCode", pack.TypeCode)
                    .WithData("Version", pack.Version)
                    .WithData("Supported", DocumentTypePackConsts.CurrentVersion);
            }
        }

        // Validate every projected type schema before touching the store. This is aggregate-wide (not one DTO
        // attribute per field) and simulates repeated packs in request order, preserving the method's unconditional
        // no-partial-write guarantee even in the non-transactional test harness.
        await ValidateSchemaPromptBudgetsAsync(input.Packs, input.Mode);

        var result = new DocumentTypePackImportResultDto();
        foreach (var pack in input.Packs)
        {
            var item = await ImportPackAsync(pack, input.Mode);
            result.Items.Add(item);

            switch (item.TypeAction)
            {
                case PackItemAction.Created: result.TypesCreated++; break;
                case PackItemAction.Updated: result.TypesUpdated++; break;
                default: result.TypesSkipped++; break;
            }

            result.FieldsCreated += item.FieldsCreated;
            result.FieldsUpdated += item.FieldsUpdated;
            result.FieldsSkipped += item.FieldsSkipped;
        }

        return result;
    }

    protected virtual async Task ValidateSchemaPromptBudgetsAsync(
        List<DocumentTypePackDto> packs,
        PackImportMode mode)
    {
        var projectedByTypeCode = new Dictionary<string, Dictionary<string, string?>>(
            StringComparer.Ordinal);

        foreach (var pack in packs)
        {
            if (!projectedByTypeCode.TryGetValue(pack.TypeCode, out var projectedFields))
            {
                projectedFields = new Dictionary<string, string?>(StringComparer.Ordinal);
                var existingType = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);
                if (existingType != null)
                {
                    var existingFields = await _fieldDefinitionRepository.GetListAsync(existingType.Id);
                    foreach (var field in existingFields)
                    {
                        projectedFields[field.Name] = field.Prompt;
                    }
                }

                projectedByTypeCode[pack.TypeCode] = projectedFields;
            }

            foreach (var field in pack.Fields)
            {
                if (!projectedFields.ContainsKey(field.Name) || mode == PackImportMode.CreateOrUpdate)
                {
                    projectedFields[field.Name] = field.Prompt;
                }
            }

            _schemaPromptBudget.EnsureCanPersist(pack.TypeCode, projectedFields.Values);
        }
    }

    protected virtual async Task<DocumentTypePackItemResultDto> ImportPackAsync(
        DocumentTypePackDto pack, PackImportMode mode)
    {
        var item = new DocumentTypePackItemResultDto { TypeCode = pack.TypeCode };

        // Match the type by code within the caller's layer (active rows only). Rename = new type (#207).
        var type = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);

        if (type == null)
        {
            // Both modes create what is missing. CheckCodeAvailableAsync is soft-delete-aware: if a deleted
            // row occupies the code it loud-fails rather than colliding on restore.
            await _documentTypeManager.CheckCodeAvailableAsync(pack.TypeCode);
            type = new DocumentType(
                GuidGenerator.Create(),
                CurrentTenant.Id,
                pack.TypeCode,
                pack.DisplayName,
                pack.Description,
                pack.ConfidenceThreshold,
                pack.Priority);
            StampProvenance(type, pack.Version);
            await _documentTypeRepository.InsertAsync(type, autoSave: true);
            item.TypeAction = PackItemAction.Created;
        }
        else if (mode == PackImportMode.CreateOrUpdate)
        {
            // Updating an existing type needs the Update permission — asserted here rather than as a blanket
            // method attribute, so a CreateOnly / all-new import never requires it.
            await CheckPolicyAsync(VaultExtractPermissions.DocumentTypes.Update);
            type.Update(pack.TypeCode, pack.DisplayName, pack.Description, pack.ConfidenceThreshold, pack.Priority);
            StampProvenance(type, pack.Version);
            await _documentTypeRepository.UpdateAsync(type, autoSave: true);
            item.TypeAction = PackItemAction.Updated;
        }
        else
        {
            // CreateOnly: leave the existing type's own properties untouched (but still add missing fields).
            item.TypeAction = PackItemAction.Skipped;
        }

        await ImportFieldsAsync(type.Id, pack.Fields, mode, pack.Version, item);
        return item;
    }

    protected virtual async Task ImportFieldsAsync(
        Guid documentTypeId,
        List<DocumentTypePackFieldDto> fields,
        PackImportMode mode,
        int version,
        DocumentTypePackItemResultDto item)
    {
        foreach (var f in fields)
        {
            var existing = await _fieldDefinitionRepository.FindByNameAsync(documentTypeId, f.Name);

            if (existing == null)
            {
                await _fieldDefinitionManager.CheckNameAvailableAsync(documentTypeId, f.Name);
                var field = new FieldDefinition(
                    GuidGenerator.Create(),
                    CurrentTenant.Id,
                    documentTypeId,
                    f.Name,
                    f.DisplayName,
                    f.Prompt,
                    f.DataType,
                    f.DisplayOrder,
                    f.IsRequired,
                    f.AllowMultiple,
                    f.IsUniqueKey);
                StampProvenance(field, version);
                await _fieldDefinitionRepository.InsertAsync(field, autoSave: true);
                item.FieldsCreated++;
            }
            else if (mode == PackImportMode.CreateOrUpdate)
            {
                // Updating an existing field needs the Update permission — asserted lazily; a CreateOnly or
                // all-new import never reaches here.
                await CheckPolicyAsync(VaultExtractPermissions.FieldDefinitions.Update);
                await GuardFieldMutationAsync(existing, f);
                existing.Update(
                    f.Name,
                    f.DisplayName,
                    f.Prompt,
                    f.DataType,
                    f.DisplayOrder,
                    f.IsRequired,
                    f.AllowMultiple,
                    f.IsUniqueKey);
                StampProvenance(existing, version);
                await _fieldDefinitionRepository.UpdateAsync(existing, autoSave: true);
                item.FieldsUpdated++;
            }
            else
            {
                item.FieldsSkipped++;
            }
        }
    }

    /// <summary>
    /// Mirror of the <see cref="FieldDefinitionAppService"/> data-safety guard: never break already-extracted
    /// values by changing a field's data type or narrowing it from multi- to single-valued. A pack that
    /// would do so loud-fails (the whole import rolls back in the ambient UoW) instead of silently
    /// corrupting stored values.
    /// </summary>
    protected virtual async Task GuardFieldMutationAsync(FieldDefinition existing, DocumentTypePackFieldDto pack)
    {
        var dataTypeChanged = pack.DataType != existing.DataType;
        var multiValueNarrowed = existing.AllowMultiple && !pack.AllowMultiple;
        if (!dataTypeChanged && !multiValueNarrowed)
        {
            return;
        }

        var hasValues = await _documentRepository.AnyExtractedFieldValueAsync(existing.Id);
        if (dataTypeChanged && hasValues)
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.DataTypeChangeNotAllowed)
                .WithData("Name", existing.Name);
        }

        if (multiValueNarrowed && hasValues)
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.MultiValueChangeNotAllowed)
                .WithData("Name", existing.Name);
        }
    }

    // Provenance: mark pack-sourced config in ExtraProperties (config metadata on the type/field aggregate,
    // not the Document truth source). A stable value keeps re-import idempotent (no phantom diffs).
    protected virtual void StampProvenance(IHasExtraProperties entity, int version)
    {
        entity.SetProperty(DocumentTypePackConsts.ProvenanceSourceKey, DocumentTypePackConsts.ProvenanceSourceValue);
        entity.SetProperty(DocumentTypePackConsts.ProvenanceVersionKey, version);
    }

    protected virtual DocumentTypePackDto MapToPack(DocumentType type, List<FieldDefinition> fields)
    {
        return new DocumentTypePackDto
        {
            Version = DocumentTypePackConsts.CurrentVersion,
            TypeCode = type.TypeCode,
            DisplayName = type.DisplayName,
            Description = type.Description,
            ConfidenceThreshold = type.ConfidenceThreshold,
            Priority = type.Priority,
            Fields = fields
                .OrderBy(f => f.DisplayOrder)
                .ThenBy(f => f.Name)
                .Select(f => new DocumentTypePackFieldDto
                {
                    Name = f.Name,
                    DisplayName = f.DisplayName,
                    Prompt = f.Prompt,
                    DataType = f.DataType,
                    DisplayOrder = f.DisplayOrder,
                    IsRequired = f.IsRequired,
                    AllowMultiple = f.AllowMultiple,
                    IsUniqueKey = f.IsUniqueKey
                })
                .ToList()
        };
    }
}

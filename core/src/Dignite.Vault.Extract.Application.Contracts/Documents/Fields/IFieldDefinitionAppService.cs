using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Field definition management (unified API for field architecture v2). Matches exactly one owning
/// layer and never mixes layers. Both layers self-manage through this AppService CRUD surface; there
/// is no seed contributor / module-startup registration path.
/// </summary>
public interface IFieldDefinitionAppService : IApplicationService
{
    /// <summary>
    /// Field definition list for the current tenant layer; never crosses layers.
    /// When <see cref="GetFieldDefinitionListInput.DocumentTypeId"/> is specified, returns only fields
    /// under that document type. When omitted (<c>null</c>), returns all field definitions in the
    /// current layer in one call, the bulk-read path used by callers such as MCP
    /// <c>vault_extract_list_document_types</c> to group in memory and eliminate per-type N+1 queries.
    /// When <see cref="GetFieldDefinitionListInput.OnlyDeleted"/> is <c>false</c>, returns active
    /// fields ordered by DisplayOrder, or by DocumentTypeId then DisplayOrder in bulk mode. When
    /// <c>true</c>, returns recycle-bin (soft-deleted) fields ordered by descending DeletionTime.
    /// </summary>
    Task<List<FieldDefinitionDto>> GetListAsync(GetFieldDefinitionListInput input);

    /// <summary>
    /// The registered field types, and what a client cannot work out for itself about each one (#562).
    /// <para>
    /// The FlexFields kernel ships no application layer, so nothing serves this by default — every
    /// downstream that builds a field designer writes this same endpoint. Modeled on the kernel's own
    /// worked example, <c>ProductFieldAppService.GetFieldTypesAsync</c> in its demo host.
    /// </para>
    /// <para>
    /// It exists so the designer's "filterable" switch is driven by the server's own
    /// <see cref="IFieldType.IndexValueType"/> (via <c>IsIndexable()</c>) instead of a copy of it
    /// maintained by hand on the client — a copy nothing in either build could catch drifting.
    /// </para>
    /// </summary>
    Task<List<FieldTypeDto>> GetFieldTypesAsync();

    Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input);

    Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Restores one soft-deleted field definition. Requires the parent <see cref="DocumentType"/>
    /// (same TenantId + TypeCode) to exist and be active. Throws
    /// <see cref="VaultExtractErrorCodes.FieldDefinition.ParentTypeMissing"/> when the parent type is
    /// missing or still deleted. Throws <see cref="VaultExtractErrorCodes.FieldDefinition.RestoreConflict"/>
    /// when an active field with the same name already exists. Use the cascade path in
    /// <see cref="IDocumentTypeAppService.RestoreAsync"/> for bulk restore.
    /// </summary>
    Task<FieldDefinitionDto> RestoreAsync(Guid id);
}

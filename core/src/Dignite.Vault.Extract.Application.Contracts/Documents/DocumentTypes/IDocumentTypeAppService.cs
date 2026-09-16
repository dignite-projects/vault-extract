using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// Document type management (field architecture v2). Matches exactly one owning layer and does not
/// union Host and Tenant. Both layers self-manage through this AppService CRUD surface; there is no
/// seed contributor / module-startup registration path.
/// </summary>
public interface IDocumentTypeAppService : IApplicationService
{
    /// <summary>
    /// The caller's own layer's active document types.
    /// </summary>
    /// <param name="includeResourcePermissions">
    /// When <c>true</c> (the default, what the operator UI wants), every returned DTO carries the caller's own
    /// per-type grants in <see cref="DocumentTypeDto.ResourcePermissions"/>, filled by ABP's
    /// <c>ResourcePermissionPopulator</c> — one multi-permission check per type. Pass <c>false</c> where the
    /// dictionary is not read: the MCP tools and resources list types for an LLM and throw it away, so they pay
    /// the populator for nothing (#629 leftover, closed by #632). The dictionary then comes back empty, which is
    /// not the same statement as "no grants" — never branch on it after passing <c>false</c>.
    /// </param>
    Task<List<DocumentTypeDto>> GetVisibleAsync(bool includeResourcePermissions = true);

    /// <summary>Soft-deleted document types in the caller's layer (recycle-bin view).</summary>
    Task<List<DocumentTypeDto>> GetDeletedAsync();

    Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input);

    Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Restores a soft-deleted document type and cascades restore to field definitions under the same
    /// (TenantId, TypeCode) that were soft-deleted with it. Throws
    /// <see cref="VaultExtractErrorCodes.DocumentType.RestoreConflict"/> when an active record with the
    /// same code already exists. Individual fields that conflict with active fields during restore are
    /// skipped defensively, although normal flows should not hit this.
    /// </summary>
    Task<DocumentTypeDto> RestoreAsync(Guid id);
}

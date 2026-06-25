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
    Task<List<DocumentTypeDto>> GetVisibleAsync();

    /// <summary>Soft-deleted document types in the caller's layer (recycle-bin view).</summary>
    Task<List<DocumentTypeDto>> GetDeletedAsync();

    Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input);

    Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Restores a soft-deleted document type and cascades restore to field definitions under the same
    /// (TenantId, TypeCode) that were soft-deleted with it. Throws
    /// <see cref="ExtractErrorCodes.DocumentType.RestoreConflict"/> when an active record with the
    /// same code already exists. Individual fields that conflict with active fields during restore are
    /// skipped defensively, although normal flows should not hit this.
    /// </summary>
    Task<DocumentTypeDto> RestoreAsync(Guid id);
}

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
    /// The caller's own layer's active document types, each carrying the caller's own per-type grants in
    /// <see cref="DocumentTypeDto.ResourcePermissions"/>, filled by ABP's <c>ResourcePermissionPopulator</c>.
    /// Callers that only need identity and display text — no per-type grants — should use
    /// <see cref="GetVisibleSummariesAsync"/> instead, which skips the populator entirely (#636).
    /// </summary>
    Task<List<DocumentTypeDto>> GetVisibleAsync();

    /// <summary>
    /// The caller's own layer's active document types, narrowed to <see cref="DocumentTypeSummaryDto"/> — no
    /// per-type grants, and therefore no <c>ResourcePermissionPopulator</c> cost. For internal callers that list
    /// types for an LLM and throw the grants away, such as the MCP tools and resources (#629 leftover, closed by
    /// #632, split out to its own narrow DTO by #636).
    /// </summary>
    Task<List<DocumentTypeSummaryDto>> GetVisibleSummariesAsync();

    /// <summary>Soft-deleted document types in the caller's layer (recycle-bin view).</summary>
    Task<List<DocumentTypeDto>> GetDeletedAsync();

    Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input);

    Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input);

    /// <summary>
    /// What switching this type's duplicate-detection scope to <paramref name="duplicateScope"/> would re-evaluate
    /// (#651 §5), so the type form can state the consequence <b>before</b> the save that enqueues reconciliation.
    /// Two counts over live documents of the type, no row loads. Gated by the same permission as
    /// <see cref="UpdateAsync"/> — it answers a question only someone who may perform the switch should be asking.
    /// </summary>
    Task<DuplicateScopePreviewDto> GetDuplicateScopePreviewAsync(Guid id, DuplicateDetectionScope duplicateScope);

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

using System;
using System.Collections.Generic;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization.Permissions.Resources;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public class DocumentTypeDto : EntityDto<Guid>, IHasResourcePermissions
{
    public Guid? TenantId { get; set; }
    public string TypeCode { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Description { get; set; }
    public double ConfidenceThreshold { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// The caller's own resource-permission grants on this type (#629), keyed by permission name — phase 1
    /// defines exactly one, <see cref="Permissions.VaultExtractResourcePermissions.Upload"/>.
    /// Populated by <c>DocumentTypeAppService.GetVisibleAsync</c> only, through ABP's
    /// <c>ResourcePermissionPopulator</c>, so the UI does not have to guess which types it may act on.
    /// Every other endpoint returning this DTO (<c>GetDeletedAsync</c>, <c>CreateAsync</c>, <c>UpdateAsync</c>,
    /// <c>RestoreAsync</c>) leaves it empty — an empty dictionary there says nothing about the caller's grants.
    /// <para>
    /// This is <b>not</b> the unconstrained extension bag CLAUDE.md forbids: it lives on a read DTO rather
    /// than on <c>Document</c>, it is never persisted, and every key is a registered permission definition —
    /// the populator enumerates them from <c>IPermissionDefinitionManager</c>, so nothing can write an
    /// arbitrary key into it.
    /// </para>
    /// </summary>
    public Dictionary<string, bool> ResourcePermissions { get; set; } = new();
}

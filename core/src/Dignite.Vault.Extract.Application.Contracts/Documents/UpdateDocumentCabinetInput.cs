using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Input for reassigning a document's owning cabinet (#257).
/// <para>
/// <see cref="CabinetId"/> = <c>null</c> means remove from cabinet (uncategorized). It intentionally
/// has no <c>[Required]</c> because null is a valid semantic value, not "missing". Non-null values
/// must reference an existing cabinet in the current layer
/// (<see cref="Volo.Abp.MultiTenancy.ICurrentTenant"/>); existence is validated by
/// <c>DocumentAppService.UpdateCabinetAsync</c>.
/// </para>
/// </summary>
public class UpdateDocumentCabinetInput
{
    /// <summary>Target cabinet Id; <c>null</c> means remove from cabinet (uncategorized).</summary>
    public Guid? CabinetId { get; set; }
}

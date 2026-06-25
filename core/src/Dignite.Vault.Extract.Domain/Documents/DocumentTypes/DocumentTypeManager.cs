using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// Domain service owning the layer-scoped uniqueness invariant of <see cref="DocumentType"/> on
/// <c>(TenantId, TypeCode)</c> (#304). After dropping the soft-delete-filtered DB unique index (not
/// portable across providers), this check is the <b>sole</b> guarantor of uniqueness, so every write
/// path (create / rename / restore) must route through it.
/// <para>
/// Layer scoping is delegated entirely to ABP's <c>IMultiTenant</c> global filter: the repository
/// queries are automatically narrowed to the ambient <see cref="Volo.Abp.MultiTenancy.ICurrentTenant"/>
/// layer (Host = <c>TenantId IS NULL</c>, tenant = its GUID). Therefore the same <c>TypeCode</c> across
/// layers is allowed (two legitimate rows), while a duplicate within one layer is rejected. No
/// <c>CurrentTenant.Id</c> predicate is hand-written.
/// </para>
/// <para>
/// Tradeoff (accepted, #304): an application-layer check has a TOCTOU race window. These are
/// low-frequency admin-managed config entities, so the window is acceptable; revisit with a
/// serializable UoW / advisory lock / portable index if a high-concurrency write path is added.
/// </para>
/// </summary>
public class DocumentTypeManager : DomainService
{
    private readonly IDocumentTypeRepository _repository;
    private readonly IDataFilter _dataFilter;

    public DocumentTypeManager(IDocumentTypeRepository repository, IDataFilter dataFilter)
    {
        _repository = repository;
        _dataFilter = dataFilter;
    }

    /// <summary>
    /// Asserts no document type in the current layer already uses <paramref name="typeCode"/>; used by
    /// create and rename. Soft-delete-aware: the <see cref="ISoftDelete"/> filter is disabled so deleted
    /// rows also occupy the code. Otherwise the path "delete -&gt; recreate same code -&gt; restore old"
    /// could yield two active rows with the same <c>(TenantId, TypeCode)</c>.
    /// </summary>
    public virtual async Task CheckCodeAvailableAsync(string typeCode)
    {
        DocumentType? existing;
        using (_dataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByTypeCodeAsync(typeCode);
        }

        if (existing != null)
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.CodeAlreadyExists)
                .WithData("TypeCode", typeCode);
        }
    }

    /// <summary>
    /// Asserts no <b>active</b> document type in the current layer already uses the code of the entity
    /// being restored. Caller restores a soft-deleted row, so here the <see cref="ISoftDelete"/> filter
    /// is (re-)enabled to consider active rows only: the soft-deleted entity itself is excluded, and any
    /// match is a genuine conflict that would otherwise create two active rows with the same code.
    /// </summary>
    public virtual async Task CheckRestorableAsync(DocumentType entity)
    {
        DocumentType? conflict;
        using (_dataFilter.Enable<ISoftDelete>())
        {
            conflict = await _repository.FindByTypeCodeAsync(entity.TypeCode);
        }

        if (conflict != null)
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.RestoreConflict)
                .WithData("TypeCode", entity.TypeCode);
        }
    }
}

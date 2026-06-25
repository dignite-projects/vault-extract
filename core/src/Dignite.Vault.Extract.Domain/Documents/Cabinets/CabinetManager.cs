using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Domain service owning the layer-scoped uniqueness invariant of <see cref="Cabinet"/> on
/// <c>(TenantId, Name)</c> (#304). After dropping the soft-delete-filtered DB unique index (not portable
/// across providers), this check is the <b>sole</b> guarantor of uniqueness, so every write path (create
/// / rename) must route through it.
/// <para>
/// Layer scoping is delegated to ABP's <c>IMultiTenant</c> global filter; the same <c>Name</c> across
/// layers is allowed, while a duplicate within one layer is rejected. No <c>CurrentTenant.Id</c>
/// predicate is hand-written.
/// </para>
/// <para>
/// Unlike <see cref="DocumentTypes.DocumentTypeManager"/> / <see cref="Fields.FieldDefinitionManager"/>,
/// the check considers <b>active rows only</b> (the default <c>ISoftDelete</c> filter is left in place):
/// Cabinet has no recycle bin / restore path, so a soft-deleted cabinet's name is intentionally free for
/// reuse — there is no "delete -&gt; restore" path that two active duplicates could break. This preserves
/// the exact behavior of the dropped <c>IsDeleted = 0</c> filtered index.
/// </para>
/// </summary>
public class CabinetManager : DomainService
{
    private readonly ICabinetRepository _repository;

    public CabinetManager(ICabinetRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Asserts no active cabinet in the current layer already uses <paramref name="name"/>; used by create and rename.</summary>
    public virtual async Task CheckNameAvailableAsync(string name)
    {
        var existing = await _repository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(ExtractErrorCodes.Cabinet.NameAlreadyExists)
                .WithData("Name", name);
        }
    }
}

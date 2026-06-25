using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Domain service owning the layer-scoped uniqueness invariant of <see cref="ExportTemplate"/> on
/// <c>(TenantId, Name)</c> (#304). After dropping the soft-delete-filtered DB unique index (not portable
/// across providers), this check is the <b>sole</b> guarantor of uniqueness, so every write path (create
/// / rename) must route through it.
/// <para>
/// Layer scoping is delegated to ABP's <c>IMultiTenant</c> global filter; the same <c>Name</c> across
/// layers is allowed, while a duplicate within one layer is rejected. No <c>CurrentTenant.Id</c>
/// predicate is hand-written.
/// </para>
/// <para>
/// Like <see cref="Cabinets.CabinetManager"/>, the check considers <b>active rows only</b>: an export
/// template has no restore path, so a soft-deleted template's name is free for reuse. This preserves the
/// exact behavior of the dropped <c>IsDeleted = 0</c> filtered index.
/// </para>
/// </summary>
public class ExportTemplateManager : DomainService
{
    private readonly IExportTemplateRepository _repository;

    public ExportTemplateManager(IExportTemplateRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Asserts no active export template in the current layer already uses <paramref name="name"/>; used by create and rename.</summary>
    public virtual async Task CheckNameAvailableAsync(string name)
    {
        var existing = await _repository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(ExtractErrorCodes.Export.TemplateNameAlreadyExists)
                .WithData("Name", name);
        }
    }
}

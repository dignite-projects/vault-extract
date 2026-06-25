using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Domain service owning the layer-scoped uniqueness invariant of <see cref="FieldDefinition"/> on
/// <c>(TenantId, DocumentTypeId, Name)</c> (#304). After dropping the soft-delete-filtered DB unique
/// index (not portable across providers), this check is the <b>sole</b> guarantor of uniqueness, so
/// every write path (create / rename / restore, including the bulk cascade restore driven by
/// <c>DocumentTypeAppService.RestoreAsync</c>) must route through it.
/// <para>
/// Layer scoping is delegated to ABP's <c>IMultiTenant</c> global filter; the same <c>Name</c> across
/// layers (or under a different type) is allowed, while a duplicate within one layer + type is rejected.
/// No <c>CurrentTenant.Id</c> predicate is hand-written. See <see cref="DocumentTypeManager"/> for the
/// accepted TOCTOU tradeoff.
/// </para>
/// </summary>
public class FieldDefinitionManager : DomainService
{
    private readonly IFieldDefinitionRepository _repository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDataFilter _dataFilter;

    public FieldDefinitionManager(
        IFieldDefinitionRepository repository,
        IDocumentTypeRepository documentTypeRepository,
        IDataFilter dataFilter)
    {
        _repository = repository;
        _documentTypeRepository = documentTypeRepository;
        _dataFilter = dataFilter;
    }

    /// <summary>
    /// Asserts no field under <paramref name="documentTypeId"/> in the current layer already uses
    /// <paramref name="name"/>; used by create and rename. Soft-delete-aware: deleted rows also occupy
    /// the name so the "delete -&gt; recreate same name -&gt; restore old" path cannot produce two active
    /// duplicates. The owning <c>TypeCode</c> for the error message is resolved only on the conflict
    /// path.
    /// </summary>
    public virtual async Task CheckNameAvailableAsync(Guid documentTypeId, string name)
    {
        FieldDefinition? existing;
        using (_dataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByNameAsync(documentTypeId, name);
        }

        if (existing != null)
        {
            throw new BusinessException(ExtractErrorCodes.FieldDefinition.AlreadyExists)
                .WithData("DocumentTypeCode", await ResolveTypeCodeAsync(documentTypeId))
                .WithData("Name", name);
        }
    }

    /// <summary>
    /// Whether an <b>active</b> field with <paramref name="name"/> already exists under
    /// <paramref name="documentTypeId"/> in the current layer. The <see cref="ISoftDelete"/> filter is
    /// (re-)enabled so soft-deleted rows are excluded — callers run inside a soft-delete-disabled scope
    /// (single-field restore / cascade restore) and need an active-only conflict signal. Returns a bool
    /// so the cascade can skip-and-log while single restore throws.
    /// </summary>
    public virtual async Task<bool> HasActiveNameConflictAsync(Guid documentTypeId, string name)
    {
        using (_dataFilter.Enable<ISoftDelete>())
        {
            return await _repository.FindByNameAsync(documentTypeId, name) != null;
        }
    }

    /// <summary>
    /// Asserts the soft-deleted <paramref name="entity"/> can be restored: no active field with the same
    /// name already exists under its type in the current layer. Throws <c>RestoreConflict</c> otherwise.
    /// </summary>
    public virtual async Task CheckRestorableAsync(FieldDefinition entity)
    {
        if (await HasActiveNameConflictAsync(entity.DocumentTypeId, entity.Name))
        {
            throw new BusinessException(ExtractErrorCodes.FieldDefinition.RestoreConflict)
                .WithData("DocumentTypeCode", await ResolveTypeCodeAsync(entity.DocumentTypeId))
                .WithData("Name", entity.Name);
        }
    }

    /// <summary>
    /// Resolves the owning type's <c>TypeCode</c> for human-readable error messages only (#207: data
    /// rows associate by immutable Id). Soft-delete traversal so the code is still resolvable if the type
    /// row is itself deleted.
    /// </summary>
    protected virtual async Task<string> ResolveTypeCodeAsync(Guid documentTypeId)
    {
        using (_dataFilter.Disable<ISoftDelete>())
        {
            var type = await _documentTypeRepository.FindAsync(documentTypeId);
            return type?.TypeCode ?? string.Empty;
        }
    }
}

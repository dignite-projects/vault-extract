using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Cabinet management (#194). Matches the current layer only and never unions across layers.
/// Permissions fail closed through method-level Cabinets.* assertions.
/// </summary>
[Authorize(VaultExtractPermissions.Cabinets.Default)]
public class CabinetAppService : VaultExtractAppService, ICabinetAppService
{
    private readonly ICabinetRepository _repository;
    private readonly IDocumentRepository _documentRepository;
    private readonly CabinetManager _cabinetManager;

    public CabinetAppService(
        ICabinetRepository repository,
        IDocumentRepository documentRepository,
        CabinetManager cabinetManager)
    {
        _repository = repository;
        _documentRepository = documentRepository;
        _cabinetManager = cabinetManager;
    }

    public virtual async Task<List<CabinetDto>> GetListAsync()
    {
        // All cabinets in the current layer, isolated by the ambient IMultiTenant filter using
        // CurrentTenant.Id.
        var list = await _repository.GetListAsync();
        return ObjectMapper.Map<List<Cabinet>, List<CabinetDto>>(list);
    }

    [Authorize(VaultExtractPermissions.Cabinets.Create)]
    public virtual async Task<CabinetDto> CreateAsync(CreateCabinetDto input)
    {
        await _cabinetManager.CheckNameAvailableAsync(input.Name);

        var entity = new Cabinet(GuidGenerator.Create(), CurrentTenant.Id, input.Name, input.Description);
        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(VaultExtractPermissions.Cabinets.Update)]
    public virtual async Task<CabinetDto> UpdateAsync(Guid id, UpdateCabinetDto input)
    {
        var entity = await _repository.GetAsync(id);

        // Cross-layer defense: callers can update only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // Name is an editable unique key. Check uniqueness only on rename; unchanged name skips the
        // check to avoid falsely treating itself as a conflict.
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await _cabinetManager.CheckNameAvailableAsync(input.Name);
        }

        entity.Update(input.Name, input.Description);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(VaultExtractPermissions.Cabinets.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // Atomically clear CabinetId on documents in this cabinet before deleting it, truly unfiling
        // them. Otherwise documents would dangle to a deleted cabinet (stale ID, and recreating a
        // cabinet with the same name cannot restore membership). Unlike DocumentType InUse protection,
        // deletion is not blocked: cabinet membership is optional organization metadata, not document
        // identity or pipeline state, so "delete cabinet" clears references rather than failing closed.
        //
        // #530: the cleanup runs with ISoftDelete disabled so RECYCLE-BIN documents are unfiled too. Clearing only
        // live documents made "delete cabinet" mean two different things depending on document lifecycle state — a
        // binned document kept hidden membership and came back filed into a deleted cabinet on restore, which active
        // cabinet reads cannot resolve, so the UI shows it as unfiled while the persisted CabinetId still points at
        // the deleted row. IMultiTenant stays on, so the cleanup never leaves this cabinet's own layer. Unfiling is
        // idempotent (setting null again changes no row), so a retry is safe. The repository performs one set-based
        // UPDATE instead of materializing every Document/Markdown payload into the delete request; cabinet cleanup
        // is explicitly allowed to be bulk state reconciliation and emits no document pipeline events.
        await _documentRepository.UnassignCabinetDocumentsAsync(entity.Id);

        await _repository.DeleteAsync(entity);
    }
}

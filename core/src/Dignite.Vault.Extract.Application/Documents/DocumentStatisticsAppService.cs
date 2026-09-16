using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Operator overview statistics (#333). Aggregates the current layer's documents into a small read-model:
/// per-lifecycle counts, the needs-review count, and the total original upload size.
/// <para>
/// Scope is handled entirely by ABP ambient filters inside the repository: <c>IMultiTenant</c> isolates the
/// current layer (active tenant -> that tenant; no tenant -> Host) and <c>ISoftDelete</c> excludes the recycle
/// bin. Neither filter is disabled, so statistics never leak across layers and never include soft-deleted rows.
/// </para>
/// </summary>
public class DocumentStatisticsAppService : VaultExtractAppService, IDocumentStatisticsAppService
{
    private readonly IDocumentRepository _documentRepository;

    public DocumentStatisticsAppService(IDocumentRepository documentRepository)
    {
        _documentRepository = documentRepository;
    }

    public virtual async Task<DocumentStatisticsDto> GetAsync()
    {
        // Programmatic assertion (not an [Authorize] attribute) to match the read-path convention of
        // DocumentAppService.GetListAsync / GetAsync.
        //
        // #632 decision 2: this is Documents.ReadAll, NOT the entry permission and NOT a per-type rule. These are
        // whole-layer aggregates — per-lifecycle counts, the needs-review count, the total upload size — and a
        // whole-layer overview has no per-type meaning; recomputing them inside one caller's type scope would be a
        // different statistic wearing the same name. A caller narrowed to some types sees the list, not the
        // overview. (The operator UI's statistics card follows, gated on ReadAll.)
        await CheckPolicyAsync(VaultExtractPermissions.Documents.ReadAll);

        var statistics = await _documentRepository.GetStatisticsAsync();
        return ObjectMapper.Map<DocumentStatisticsModel, DocumentStatisticsDto>(statistics);
    }
}

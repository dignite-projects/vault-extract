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
public class DocumentStatisticsAppService : ExtractAppService, IDocumentStatisticsAppService
{
    private readonly IDocumentRepository _documentRepository;

    public DocumentStatisticsAppService(IDocumentRepository documentRepository)
    {
        _documentRepository = documentRepository;
    }

    public virtual async Task<DocumentStatisticsDto> GetAsync()
    {
        // Programmatic assertion (not an [Authorize] attribute) to match the read-path convention of
        // DocumentAppService.GetListAsync / GetAsync. Same permission as the document list: if you can see the
        // list, you can see its aggregate counts.
        await CheckPolicyAsync(ExtractPermissions.Documents.Default);

        var statistics = await _documentRepository.GetStatisticsAsync();
        return ObjectMapper.Map<DocumentStatisticsModel, DocumentStatisticsDto>(statistics);
    }
}

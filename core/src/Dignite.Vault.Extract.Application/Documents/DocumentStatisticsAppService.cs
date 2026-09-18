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
    private readonly DocumentAccessChecker _documentAccess;

    public DocumentStatisticsAppService(
        IDocumentRepository documentRepository,
        DocumentAccessChecker documentAccess)
    {
        _documentRepository = documentRepository;
        _documentAccess = documentAccess;
    }

    public virtual async Task<DocumentStatisticsDto> GetAsync()
    {
        // Programmatic assertion (not an [Authorize] attribute) to match the read-path convention of
        // DocumentAppService.GetListAsync / GetAsync.
        //
        // #635: the Statistics row of the rule table — Documents.ReadAll, no per-type arm, no owner arm, and now
        // entry alongside. These are whole-layer aggregates (per-lifecycle counts, the needs-review count that
        // feeds the list page's badge, the total upload size); a whole-layer overview has no per-type — or
        // per-uploader — meaning, and recomputing it inside one caller's scope would be a different statistic
        // wearing the same name. A caller narrowed to some types sees the list, not the overview.
        await _documentAccess.CheckAsync(DocumentAccessRule.Statistics, DocumentAccessSubject.None);

        var statistics = await _documentRepository.GetStatisticsAsync();
        return ObjectMapper.Map<DocumentStatisticsModel, DocumentStatisticsDto>(statistics);
    }
}

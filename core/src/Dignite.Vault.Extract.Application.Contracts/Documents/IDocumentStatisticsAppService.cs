using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Operator overview statistics (#333): a small read-only aggregate over the current layer's documents,
/// surfaced on the Dignite Vault Extract overview home. Kept separate from <see cref="IDocumentAppService"/> so that
/// service stays focused. Gated by <c>VaultExtractPermissions.Documents.ReadAll</c> (#632): these are whole-layer
/// aggregates, and a whole-layer overview has no per-type meaning, so a caller narrowed to per-type <c>Read</c>
/// grants sees the list but not the overview.
/// </summary>
public interface IDocumentStatisticsAppService : IApplicationService
{
    Task<DocumentStatisticsDto> GetAsync();
}

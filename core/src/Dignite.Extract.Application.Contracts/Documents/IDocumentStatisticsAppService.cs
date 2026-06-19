using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Extract.Documents;

/// <summary>
/// Operator overview statistics (#333): a small read-only aggregate over the current layer's documents,
/// surfaced on the Dignite Extract overview home. Kept separate from <see cref="IDocumentAppService"/> so that
/// service stays focused. Gated by <c>ExtractPermissions.Documents.Default</c> (same as the list).
/// </summary>
public interface IDocumentStatisticsAppService : IApplicationService
{
    Task<DocumentStatisticsDto> GetAsync();
}

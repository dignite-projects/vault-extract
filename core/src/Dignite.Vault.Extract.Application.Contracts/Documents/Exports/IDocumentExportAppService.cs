using System.Threading.Tasks;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// The human-facing egress (#414 rationale): a deterministic, no-LLM-in-the-loop download of the channel's own
/// truth, for an operator who has no AI client and no code. Everything it emits is a projection of what the
/// document list already shows — nothing is transformed, nothing is preset, and industry format knowledge
/// (仕訳 layouts, account mapping, tax split) stays downstream.
/// </summary>
public interface IDocumentExportAppService : IApplicationService
{
    Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input);
}

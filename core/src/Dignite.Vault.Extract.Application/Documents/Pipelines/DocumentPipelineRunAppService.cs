using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Implementation of <see cref="IDocumentPipelineRunAppService"/> (#216).
/// Authorization: explicit <c>CheckPolicyAsync(Documents.Default)</c>, matching
/// <c>DocumentAppService</c>, because <c>[Authorize]</c> does not fire on reflection / LLM tool paths.
/// Tenant isolation: ABP <c>IMultiTenant</c> global filter applies automatically.
/// </summary>
public class DocumentPipelineRunAppService : ExtractAppService, IDocumentPipelineRunAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineRunToDocumentPipelineRunDtoMapper _runMapper;

    public DocumentPipelineRunAppService(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunToDocumentPipelineRunDtoMapper runMapper)
    {
        _documentRepository = documentRepository;
        _runRepository = runRepository;
        _runMapper = runMapper;
    }

    public virtual async Task<List<DocumentPipelineRunDto>> GetListAsync(Guid documentId)
    {
        await CheckPolicyAsync(ExtractPermissions.Documents.Default);

        // Fail-closed safety gate: assert visibility through the document read path before returning
        // its orchestration state. CheckPolicyAsync alone is insufficient. PipelineRun has its own
        // IMultiTenant filter but does not implement ISoftDelete; DB-level CASCADE only clears rows on
        // hard delete, so runs for soft-deleted Documents still exist in the child table. Without the
        // Document.GetAsync assertion, a caller who guesses a same-tenant documentId that is
        // soft-deleted or hidden by future visibility rules could read its orchestration metadata from
        // this endpoint (orphan disclosure). GetAsync applies both ISoftDelete and IMultiTenant
        // filters: not found -> EntityNotFoundException -> 404, matching the contract.
        _ = await _documentRepository.GetAsync(documentId, includeDetails: false);

        var runs = await _runRepository.GetListByDocumentAsync(documentId);
        // Call the child mapper Map(source) directly instead of ObjectMapper so AfterMap decodes
        // Candidates, matching the original [UseMapper] nested-path behavior; see
        // ExtractApplicationMappers comments.
        return runs.Select(_runMapper.Map).ToList();
    }
}

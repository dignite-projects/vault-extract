using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Implementation of <see cref="IDocumentPipelineRunAppService"/> (#216).
/// Authorization: explicit <c>CheckPolicyAsync(Documents.Default)</c> for entry, then the #632 per-type Read rule
/// against the document itself — matching <c>DocumentAppService.GetAsync</c>, and programmatic because
/// <c>[Authorize]</c> does not fire on reflection / LLM tool paths.
/// Tenant isolation: ABP <c>IMultiTenant</c> global filter applies automatically.
/// </summary>
public class DocumentPipelineRunAppService : VaultExtractAppService, IDocumentPipelineRunAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineRunToDocumentPipelineRunDtoMapper _runMapper;
    private readonly DocumentAccessChecker _documentAccess;

    public DocumentPipelineRunAppService(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunToDocumentPipelineRunDtoMapper runMapper,
        DocumentAccessChecker documentAccess)
    {
        _documentRepository = documentRepository;
        _runRepository = runRepository;
        _runMapper = runMapper;
        _documentAccess = documentAccess;
    }

    public virtual async Task<List<DocumentPipelineRunDto>> GetListAsync(Guid documentId)
    {
        // #635: entry is asserted before the load — see DocumentAppService.GetAsync. A bare entry check, not a
        // resolved scope: judging one document needs at most one grant check, never a sweep of the layer.
        await _documentAccess.CheckEntryAsync();

        // Fail-closed safety gate: assert visibility through the document read path before returning
        // its orchestration state. CheckPolicyAsync alone is insufficient. PipelineRun has its own
        // IMultiTenant filter but does not implement ISoftDelete; DB-level CASCADE only clears rows on
        // hard delete, so runs for soft-deleted Documents still exist in the child table. Without the
        // Document.GetAsync assertion, a caller who guesses a same-tenant documentId that is
        // soft-deleted or hidden by future visibility rules could read its orchestration metadata from
        // this endpoint (orphan disclosure). GetAsync applies both ISoftDelete and IMultiTenant
        // filters: not found -> EntityNotFoundException -> 404, matching the contract.
        var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

        // Orchestration state is part of reading the document, so it rides the same rule as
        // DocumentAppService.GetAsync — Documents.ReadAll, a Read grant on this document's own type, or this
        // caller uploaded it. Without this, a caller narrowed to one type could read the pipeline history of
        // every document in the layer by id.
        await _documentAccess.CheckAsync(DocumentAccessRule.Read, DocumentAccessSubject.Of(document));

        var runs = await _runRepository.GetListByDocumentAsync(documentId);
        // Call the child mapper Map(source) directly instead of ObjectMapper so AfterMap decodes
        // Candidates, matching the original [UseMapper] nested-path behavior; see
        // VaultExtractApplicationMappers comments.
        return runs.Select(_runMapper.Map).ToList();
    }
}

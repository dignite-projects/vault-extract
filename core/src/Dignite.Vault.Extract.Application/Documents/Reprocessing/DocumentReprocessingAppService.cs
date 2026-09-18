using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.Reprocessing;
using Dignite.Vault.Extract.Permissions;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.Reprocessing;

/// <summary>
/// Application-layer entry point for bulk reprocessing of existing documents (#289), covering manual
/// trigger + preview + chained dispatch + idempotent per-document execution. Triggering enqueues only
/// one dispatcher and returns immediately; the dispatcher keyset-paginates the scope in the
/// background and enqueues per-document jobs in batches.
/// <para>
/// Security (#635): each method's first act is <c>DocumentAccessChecker.CheckAsync</c> with its own row of
/// <see cref="DocumentAccessRule"/>'s table — <c>ReprocessFieldExtraction</c> or
/// <c>ReprocessReclassification</c>. Both are module-wide only, by decision (admin-level bulk over a whole
/// type), and both now require <b>entry</b> alongside: the <c>[Authorize]</c> attributes these methods used to
/// carry never asserted it, so a principal holding <c>Documents.Reprocessing.*</c> without
/// <c>VaultExtract.Documents</c> could re-run a whole type in an area it could not open. There is no
/// <c>[Authorize]</c> on this class or any of its methods, and a structural test enforces that.
/// </para>
/// <para>
/// Scope count / enumeration is automatically isolated by the ABP <c>IMultiTenant</c> global filter using
/// <see cref="ApplicationService.CurrentTenant"/>; no handwritten TenantId predicates are used. The
/// dispatcher restores the ambient layer from the passed <c>CurrentTenant.Id</c>.
/// </para>
/// </summary>
public class DocumentReprocessingAppService : VaultExtractAppService, IDocumentReprocessingAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DocumentAccessChecker _documentAccess;

    public DocumentReprocessingAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldRepository fieldRepository,
        IBackgroundJobManager backgroundJobManager,
        DocumentAccessChecker documentAccess)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldRepository = fieldRepository;
        _backgroundJobManager = backgroundJobManager;
        _documentAccess = documentAccess;
    }

    public virtual async Task<FieldReextractionPreviewDto> PreviewFieldExtractionAsync(Guid documentTypeId)
    {
        await _documentAccess.CheckAsync(
            DocumentAccessRule.ReprocessFieldExtraction, DocumentAccessSubject.None);

        await EnsureTypeInCurrentLayerAsync(documentTypeId);

        var count = await _documentRepository.CountForReprocessingAsync(
            documentTypeId, withReason: null, excludeManuallyConfirmed: false);
        var definitions = await _fieldRepository.GetListAsync(documentTypeId);

        return new FieldReextractionPreviewDto
        {
            DocumentTypeId = documentTypeId,
            DocumentCount = count,
            FieldNames = definitions.Select(d => d.Name).ToList()
        };
    }

    public virtual async Task<ReprocessingStartResultDto> StartFieldExtractionAsync(StartFieldReextractionInput input)
    {
        await _documentAccess.CheckAsync(
            DocumentAccessRule.ReprocessFieldExtraction, DocumentAccessSubject.None);

        await EnsureTypeInCurrentLayerAsync(input.DocumentTypeId);

        var count = await _documentRepository.CountForReprocessingAsync(
            input.DocumentTypeId, withReason: null, excludeManuallyConfirmed: false);

        Logger.LogInformation(
            "StartFieldExtraction user={UserId} tenant={TenantId} type={DocumentTypeId} estimatedCount={Count}",
            CurrentUser.Id, CurrentTenant.Id, input.DocumentTypeId, count);

        await _backgroundJobManager.EnqueueAsync(
            new DocumentFieldReextractionDispatcherArgs
            {
                DocumentTypeId = input.DocumentTypeId,
                TenantId = CurrentTenant.Id,
                AfterId = null
            });

        return new ReprocessingStartResultDto { EstimatedDocumentCount = count };
    }

    public virtual async Task<ReclassificationPreviewDto> PreviewReclassificationAsync(ReclassificationScopeInput input)
    {
        await _documentAccess.CheckAsync(
            DocumentAccessRule.ReprocessReclassification, DocumentAccessSubject.None);

        var (typeId, withReason, excludeConfirmed) = await ResolveScopeAsync(input);

        var count = await _documentRepository.CountForReprocessingAsync(typeId, withReason, excludeConfirmed);

        return new ReclassificationPreviewDto { DocumentCount = count };
    }

    public virtual async Task<ReprocessingStartResultDto> StartReclassificationAsync(ReclassificationScopeInput input)
    {
        await _documentAccess.CheckAsync(
            DocumentAccessRule.ReprocessReclassification, DocumentAccessSubject.None);

        var (typeId, withReason, excludeConfirmed) = await ResolveScopeAsync(input);

        var count = await _documentRepository.CountForReprocessingAsync(typeId, withReason, excludeConfirmed);

        Logger.LogInformation(
            "StartReclassification user={UserId} tenant={TenantId} scope={Scope} type={DocumentTypeId} withReason={WithReason} excludeConfirmed={ExcludeConfirmed} estimatedCount={Count}",
            CurrentUser.Id, CurrentTenant.Id, input.Scope, typeId, withReason, excludeConfirmed, count);

        await _backgroundJobManager.EnqueueAsync(
            new DocumentReclassificationDispatcherArgs
            {
                DocumentTypeId = typeId,
                WithReason = withReason,
                ExcludeManuallyConfirmed = excludeConfirmed,
                TenantId = CurrentTenant.Id,
                AfterId = null
            });

        return new ReprocessingStartResultDto { EstimatedDocumentCount = count };
    }

    /// <summary>Translates the scope DTO into the repository range-query triple and validates that OnlyCurrentType exists in the current layer.</summary>
    protected virtual async Task<(Guid? TypeId, DocumentReviewReasons? WithReason, bool ExcludeConfirmed)> ResolveScopeAsync(
        ReclassificationScopeInput input)
    {
        switch (input.Scope)
        {
            case ReclassificationScope.OnlyCurrentType:
                // DocumentTypeId requiredness is guaranteed by DTO IValidatableObject; validate here
                // that it exists in the current layer.
                await EnsureTypeInCurrentLayerAsync(input.DocumentTypeId!.Value);
                return (input.DocumentTypeId, null, !input.IncludeManuallyConfirmed);

            case ReclassificationScope.AllDocuments:
                return (null, null, !input.IncludeManuallyConfirmed);

            case ReclassificationScope.PendingReviewQueue:
                // Pending review queue = unresolved classification (#284 two-axis model:
                // UnresolvedClassification reason, replacing old PendingReview). These documents have
                // no confirmed type, so IncludeManuallyConfirmed is meaningless.
                return (null, DocumentReviewReasons.UnresolvedClassification, false);

            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Scope, "Unknown reclassification scope.");
        }
    }

    /// <summary>Validates that the document type exists in the current ambient layer; cross-layer / nonexistent IDs throw <see cref="EntityNotFoundException"/>.</summary>
    protected virtual async Task EnsureTypeInCurrentLayerAsync(Guid documentTypeId)
    {
        // FindAsync is isolated by the ambient IMultiTenant filter, so cross-layer IDs return null.
        _ = await _documentTypeRepository.FindAsync(documentTypeId)
            ?? throw new EntityNotFoundException(typeof(DocumentType), documentTypeId);
    }
}

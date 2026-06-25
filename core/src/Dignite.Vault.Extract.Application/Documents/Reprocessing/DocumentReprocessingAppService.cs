using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.Reprocessing;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
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
/// Security: admin-level <see cref="ExtractPermissions.Documents.Reprocessing"/> permission. Scope
/// count / enumeration is automatically isolated by the ABP <c>IMultiTenant</c> global filter using
/// <see cref="ApplicationService.CurrentTenant"/>; no handwritten TenantId predicates are used. The
/// dispatcher restores the ambient layer from the passed <c>CurrentTenant.Id</c>.
/// </para>
/// </summary>
public class DocumentReprocessingAppService : ExtractAppService, IDocumentReprocessingAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentReprocessingAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        IBackgroundJobManager backgroundJobManager)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _backgroundJobManager = backgroundJobManager;
    }

    [Authorize(ExtractPermissions.Documents.Reprocessing.FieldExtraction)]
    public virtual async Task<FieldReextractionPreviewDto> PreviewFieldExtractionAsync(Guid documentTypeId)
    {
        await EnsureTypeInCurrentLayerAsync(documentTypeId);

        var count = await _documentRepository.CountForReprocessingAsync(
            documentTypeId, withReason: null, excludeManuallyConfirmed: false);
        var definitions = await _fieldDefinitionRepository.GetListAsync(documentTypeId);

        return new FieldReextractionPreviewDto
        {
            DocumentTypeId = documentTypeId,
            DocumentCount = count,
            FieldNames = definitions.Select(d => d.Name).ToList()
        };
    }

    [Authorize(ExtractPermissions.Documents.Reprocessing.FieldExtraction)]
    public virtual async Task<ReprocessingStartResultDto> StartFieldExtractionAsync(StartFieldReextractionInput input)
    {
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

    [Authorize(ExtractPermissions.Documents.Reprocessing.Reclassification)]
    public virtual async Task<ReclassificationPreviewDto> PreviewReclassificationAsync(ReclassificationScopeInput input)
    {
        var (typeId, withReason, excludeConfirmed) = await ResolveScopeAsync(input);

        var count = await _documentRepository.CountForReprocessingAsync(typeId, withReason, excludeConfirmed);

        return new ReclassificationPreviewDto { DocumentCount = count };
    }

    [Authorize(ExtractPermissions.Documents.Reprocessing.Reclassification)]
    public virtual async Task<ReprocessingStartResultDto> StartReclassificationAsync(ReclassificationScopeInput input)
    {
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

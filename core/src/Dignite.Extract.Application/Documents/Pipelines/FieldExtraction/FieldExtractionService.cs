using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Dignite.Extract.Documents.Review;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction execution engine (#289 step 1). Extracts the core action that used to be inline in
/// <see cref="FieldExtractionEventHandler"/> into a reusable unit:
/// "read field definitions -> <see cref="FieldExtractionWorkflow.ExtractAsync"/> -> in-flight guard -> <c>Document.SetFields</c>
/// -> publish <see cref="FieldsExtractedEto"/>", shared by two trigger types:
/// <list type="bullet">
///   <item>classification-completed event cascade (<see cref="FieldExtractionEventHandler"/>, which keeps event-layer stale / cross-tenant guards before delegating to this engine);</item>
///   <item>bulk / single-document "field re-extraction" reprocessing (<c>field-extraction</c> pipeline background job, #289 steps 2-4).</item>
/// </list>
/// <para>
/// The engine extracts against the <b>Document's current <see cref="Document.DocumentTypeId"/></b> (#207). Callers only provide
/// <paramref name="documentId"/> + <paramref name="tenantId"/> and do not need to know the type. <paramref name="expectedEventTypeCode"/>
/// is supplied only by the event path for stale reclassify event early-exit optimization: if the old TypeCode carried by the event resolves
/// to a type different from the current Document type, skip it and wait for the new event to trigger the next run. Bulk paths pass <c>null</c>
/// and always extract against the current type.
/// </para>
/// <para>
/// Security constraints (CLAUDE.md "Security covenant"): explicitly restore target TenantId context with <see cref="ICurrentTenant.Change"/>,
/// letting ABP <c>IMultiTenant</c> filters isolate repository queries by layer; assert cross-tenant safety to defend against disabled ambient filters;
/// assert the in-flight reclassify race (type changed while LLM was in flight -> discard, preventing old schema from polluting ExtractedFields).
/// </para>
/// <para>
/// Three-phase UoW pattern (<c>.claude/rules/background-jobs.md</c>): read FieldDefinition / reload Document.Markdown /
/// LLM call / write Document + publish, with short <c>requiresNew</c> UoWs around each persistence phase. External LLM calls are never wrapped in a long transaction.
/// Callers (event handler / background job) must call this method with ambient UoW disabled (<c>[UnitOfWork(IsDisabled = true)]</c>)
/// or from an independent short-UoW context.
/// </para>
/// </summary>
public class FieldExtractionService : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ReviewStateEvaluator _reviewEvaluator;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    // On background-job paths, ABP's BackgroundJobExecuter pushes the job execution cancellation token through
    // ICancellationTokenProvider.Use(...), same as DocumentParseBackgroundJob. Event paths fall back to
    // CancellationToken.None when no ambient token exists, preserving behavior.
    private readonly ICancellationTokenProvider _cancellationTokenProvider;
    private readonly ILogger<FieldExtractionService> _logger;

    public FieldExtractionService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ReviewStateEvaluator reviewEvaluator,
        FieldExtractionWorkflow workflow,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ICancellationTokenProvider cancellationTokenProvider,
        ILogger<FieldExtractionService> logger)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _reviewEvaluator = reviewEvaluator;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _cancellationTokenProvider = cancellationTokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one complete field extraction for a single document using its current type (whole-set replacement + publish <see cref="FieldsExtractedEto"/>).
    /// Idempotent: repeated calls produce the same result for the same final state, and redelivery is harmless (#289 "idempotency is the foundation").
    /// If any precondition guard fails (missing document / cross-tenant / unclassified / stale event / reclassified while in flight),
    /// returns <see cref="FieldExtractionOutcome.Skipped"/> without writing or publishing.
    /// </summary>
    /// <param name="documentId">Target document Id.</param>
    /// <param name="tenantId">Tenant owning the target document, which decides the field-definition layer; the engine calls <see cref="ICurrentTenant.Change"/> with it.</param>
    /// <param name="expectedEventTypeCode">Old TypeCode supplied by the event path for stale event early exit; bulk paths pass <c>null</c>.</param>
    public virtual async Task<FieldExtractionResult> ExtractAsync(
        Guid documentId,
        Guid? tenantId,
        string? expectedEventTypeCode = null)
    {
        // Explicitly restore target tenant context. In Hangfire / worker contexts, background jobs and event handlers
        // do not necessarily restore ICurrentTenant automatically.
        using (_currentTenant.Change(tenantId))
        {
            // Phase 1: short UoW. Read type / field definitions using the Document's current internal DocumentTypeId (#207).
            // Explicit disposal fully exits this UoW before entering the phase 2 external LLM call.
            Guid documentTypeId;
            string documentTypeCode;
            List<FieldDefinition> definitions;
            string markdown;
            using (var readUow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                var readDocument = await _documentRepository.FindAsync(documentId, includeDetails: false);
                if (readDocument == null)
                {
                    _logger.LogWarning(
                        "Field extraction requested for missing document {DocumentId} — skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                // Cross-tenant assertion, defending paths where the ambient DataFilter was disabled.
                if (readDocument.TenantId != tenantId)
                {
                    _logger.LogWarning(
                        "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                        tenantId, readDocument.TenantId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                if (!readDocument.DocumentTypeId.HasValue)
                {
                    _logger.LogInformation(
                        "Field extraction requested for unclassified document {DocumentId}; skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                documentTypeId = readDocument.DocumentTypeId.Value;

                var currentType = await _documentTypeRepository.FindAsync(documentTypeId, includeDetails: false);
                if (currentType == null)
                {
                    _logger.LogWarning(
                        "Document {DocumentId} references missing DocumentTypeId {DocumentTypeId}; field extraction skipped.",
                        documentId, documentTypeId);
                    return FieldExtractionResult.Skipped;
                }

                documentTypeCode = currentType.TypeCode;

                // Event path only: stale reclassify event early-exit optimization. If the old TypeCode carried by the event resolves
                // to a type different from the current Document type, this event is stale (reclassified while in flight); skip it and wait for the new event.
                // Bulk paths pass expectedEventTypeCode=null and always extract against the current type without this early exit.
                if (expectedEventTypeCode != null)
                {
                    var eventType = await _documentTypeRepository.FindByTypeCodeAsync(expectedEventTypeCode);
                    if (eventType != null && eventType.Id != documentTypeId)
                    {
                        _logger.LogInformation(
                            "Stale classification event before field extraction: event typeCode={EventTypeCode} (typeId={EventTypeId}) " +
                            "document typeId={DocTypeId} doc={DocumentId}.",
                            expectedEventTypeCode, eventType.Id, documentTypeId, documentId);
                        return FieldExtractionResult.Skipped;
                    }

                    if (eventType == null && !string.Equals(expectedEventTypeCode, documentTypeCode, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "Classification event typeCode={EventTypeCode} is no longer resolvable in tenant {TenantId}; " +
                            "continuing field extraction for doc {DocumentId} with current typeCode={CurrentTypeCode} and stable typeId={DocumentTypeId}.",
                            expectedEventTypeCode, tenantId, documentId, documentTypeCode, documentTypeId);
                    }
                }

                definitions = await _fieldDefinitionRepository.GetListAsync(documentTypeId);
                markdown = readDocument.Markdown ?? string.Empty;
                await readUow.CompleteAsync();
            }

            // Empty-field path: target type has no field definitions. Still clear any old schema field rows that may remain on this document.
            // When reclassifying from a type with fields to a type without fields, keeping old rows would make structured search / DTOs
            // incorrectly carry them under the new TypeCode, violating the "reclassify replaces the whole set and leaves no old schema residue" semantics.
            // Clear and publish inside a short UoW.
            if (definitions.Count == 0)
            {
                using var clearUow = _unitOfWorkManager.Begin(requiresNew: true);

                var blankDocument = await _documentRepository.FindWithFieldValuesAsync(documentId);
                if (blankDocument == null)
                {
                    _logger.LogWarning(
                        "Field extraction requested for missing document {DocumentId} — skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                if (blankDocument.TenantId != tenantId)
                {
                    _logger.LogWarning(
                        "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                        tenantId, blankDocument.TenantId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                // Clear only when the current type is still the one captured in phase 1, meaning this is not a stale event from a reclassify race.
                // This avoids using a stale event to accidentally delete fields written by a later classification. Compare by internal DocumentTypeId (#207).
                if (blankDocument.DocumentTypeId != documentTypeId)
                {
                    _logger.LogInformation(
                        "Stale field extraction while clearing empty fields: document typeId={DocTypeId} expected typeId={ExpectedTypeId} doc={DocumentId}.",
                        blankDocument.DocumentTypeId, documentTypeId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                // #284: no field definitions implies no required fields, so clear MissingRequiredFields when reclassifying to a type without fields.
                // #411: a type with no fields has no unique key, so there is no fingerprint and no duplicate basis — clear both too.
                var hadFields = blankDocument.ExtractedFieldValues.Count > 0;
                var hadMissingRequired =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.MissingRequiredFields) != DocumentReviewReasons.None;
                var hadFingerprint = blankDocument.FieldFingerprint != null;
                var hadDuplicateSuspected =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None;
                if (hadFields || hadMissingRequired || hadFingerprint || hadDuplicateSuspected)
                {
                    blankDocument.SetFields(Array.Empty<DocumentFieldValue>());
                    blankDocument.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
                    blankDocument.SetFieldFingerprint(null);
                    blankDocument.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: false);
                    await _documentRepository.UpdateAsync(blankDocument, autoSave: true);
                }

                await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount: 0, documentTypeCode);
                await clearUow.CompleteAsync();
                return FieldExtractionResult.Cleared;
            }

            var descriptors = definitions.Select(d => new FieldExtractionDescriptor(
                d.Id, d.Name, d.Prompt, d.DataType, d.IsRequired, d.AllowMultiple)).ToList();

            // Phase 2: external LLM call, **outside any UoW** (hard constraint from background-jobs.md).
            // At this point the short phase 1 UoW has been disposed, so _unitOfWorkManager.Current should be null.
            if (_unitOfWorkManager.Current != null)
            {
                _logger.LogWarning(
                    "FieldExtractionService entered external LLM call with ambient UoW present (doc={DocumentId}). " +
                    "This violates background-jobs.md (external work must not run inside a long-lived UoW). " +
                    "Check the caller's UoW boundaries and readUow dispose ordering.",
                    documentId);
            }

            var extracted = await _workflow.ExtractAsync(descriptors, markdown, _cancellationTokenProvider.Token);

            // Phase 3: short UoW writes Document + publishes FieldsExtractedEto. ABP outbox persists both atomically in the same UoW,
            // avoiding "field write succeeded but event was lost".
            using var writeUow = _unitOfWorkManager.Begin(requiresNew: true);

            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning(
                    "Field extraction requested for missing document {DocumentId} — skipped.",
                    documentId);
                return FieldExtractionResult.Skipped;
            }

            if (document.TenantId != tenantId)
            {
                _logger.LogWarning(
                    "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                    tenantId, document.TenantId, documentId);
                return FieldExtractionResult.Skipped;
            }

            // In-flight reclassify race assertion: if the Document's current DocumentTypeId no longer matches the type Id captured in phase 1,
            // the document was reclassified while the LLM was in flight. Continuing would pollute ExtractedFields with the old schema, so discard this run.
            if (document.DocumentTypeId != documentTypeId)
            {
                _logger.LogInformation(
                    "Reclassified during field extraction: captured typeId={CapturedTypeId} current typeId={DocTypeId} doc={DocumentId}. " +
                    "Discarding to avoid writing fields against an outdated schema.",
                    documentTypeId, document.DocumentTypeId, documentId);
                return FieldExtractionResult.Skipped;
            }

            // During the LLM call, admins may rename, change type, or delete field definitions. Reread once by stable Id before writing.
            var currentDefinitions = await _fieldDefinitionRepository.GetListAsync(documentTypeId);
            var currentDefinitionsById = currentDefinitions.ToDictionary(d => d.Id);

            var fieldValues = new List<DocumentFieldValue>();
            foreach (var d in descriptors)
            {
                if (!extracted.TryGetValue(d.Name, out var value) || !value.HasValue)
                {
                    continue;
                }

                if (!currentDefinitionsById.TryGetValue(d.FieldDefinitionId, out var currentDefinition))
                {
                    _logger.LogInformation(
                        "FieldDefinition {FieldDefinitionId} was removed or disabled during extraction for doc {DocumentId}; extracted value skipped.",
                        d.FieldDefinitionId, documentId);
                    continue;
                }

                if (currentDefinition.DataType != d.DataType)
                {
                    _logger.LogWarning(
                        "FieldDefinition {FieldDefinitionId} DataType changed during extraction for doc {DocumentId}: {OldDataType} -> {NewDataType}; stale value skipped.",
                        d.FieldDefinitionId, documentId, d.DataType, currentDefinition.DataType);
                    continue;
                }

                if (currentDefinition.AllowMultiple != d.AllowMultiple)
                {
                    _logger.LogWarning(
                        "FieldDefinition {FieldDefinitionId} AllowMultiple changed during extraction for doc {DocumentId}: {OldAllowMultiple} -> {NewAllowMultiple}; stale value skipped.",
                        d.FieldDefinitionId, documentId, d.AllowMultiple, currentDefinition.AllowMultiple);
                    continue;
                }

                if (!ExtractedFieldValueValidator.IsValid(value.Value, currentDefinition.DataType, currentDefinition.AllowMultiple))
                {
                    _logger.LogWarning(
                        "FieldExtractionWorkflow returned an invalid {DataType} (multi={AllowMultiple}) value for field {FieldName} ({FieldDefinitionId}) on doc {DocumentId}; value skipped.",
                        currentDefinition.DataType, currentDefinition.AllowMultiple, currentDefinition.Name, currentDefinition.Id, documentId);
                    continue;
                }

                fieldValues.AddRange(DocumentFieldValueFactory.Expand(
                    currentDefinition.Id, currentDefinition.DataType, currentDefinition.AllowMultiple, value.Value));
            }

            document.SetFields(fieldValues);

            // #284: evaluate required-field missingness at the moment extraction completes and materialize MissingRequiredFields.
            // Read paths cannot know whether extraction already ran, so this must be decided at write time.
            // Reuse currentDefinitions reread before writing, filtered by IsRequired, plus the written ExtractedFieldValues.
            // This is after the stale guard, so stale events cannot reach here and no new race is introduced.
            // Non-blocking: setting MRF does not move an already Ready document back, because DeriveLifecycle checks only blocking reasons.
            var requiredIds = currentDefinitions.Where(cd => cd.IsRequired).Select(cd => cd.Id).ToList();
            var extractedIds = document.ExtractedFieldValues.Select(v => v.FieldDefinitionId).Distinct().ToList();
            document.SetReviewReason(
                DocumentReviewReasons.MissingRequiredFields,
                _reviewEvaluator.MissingRequiredFieldsPresent(requiredIds, extractedIds));

            // #411: compute the duplicate fingerprint from this type's unique-key fields, then flag a suspected
            // duplicate re-upload. The fingerprint is derived from the just-written field values; a collision with
            // another document in the same layer + type sets the blocking DuplicateSuspected reason. Because
            // field-extraction is a key pipeline (#411), the run's Ready derivation in DocumentFieldExtractionBackgroundJob
            // then withholds DocumentReadyEto until an operator resolves it. DuplicateAllowed (the operator's prior
            // "not a duplicate" override) suppresses re-flagging on re-extraction. The collision query relies on the
            // ambient IMultiTenant + ISoftDelete filters (tenant restored via ICurrentTenant.Change above) and is
            // hard-capped, so it never returns a cross-layer or unbounded set.
            var fingerprint = FieldFingerprintCalculator.Compute(document.ExtractedFieldValues, currentDefinitions);
            document.SetFieldFingerprint(fingerprint);

            var duplicateSuspected = false;
            if (fingerprint != null && !document.DuplicateAllowed)
            {
                var candidates = await _documentRepository.FindDuplicateCandidatesAsync(
                    document.Id,
                    documentTypeId,
                    fingerprint,
                    DocumentConsts.MaxDuplicateCandidates,
                    _cancellationTokenProvider.Token);
                duplicateSuspected = candidates.Count > 0;
            }

            document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, duplicateSuspected);

            // FieldsExtractedEto.FieldCount is the logical field count (distinct fields with values), not the expanded row count.
            var fieldCount = fieldValues.Select(v => v.FieldDefinitionId).Distinct().Count();

            await _documentRepository.UpdateAsync(document, autoSave: true);
            await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount, documentTypeCode);

            await writeUow.CompleteAsync();

            _logger.LogInformation(
                "Field extraction for document {DocumentId} produced {NonNullCount}/{TotalCount} non-null fields ({RowCount} value rows).",
                documentId, fieldCount, definitions.Count, fieldValues.Count);

            return FieldExtractionResult.Extracted(fieldCount);
        }
    }

    private async Task PublishFieldsExtractedAsync(Guid documentId, Guid? tenantId, int fieldCount, string documentTypeCode)
    {
        await _distributedEventBus.PublishAsync(
            new FieldsExtractedEto
            {
                DocumentId = documentId,
                TenantId = tenantId,
                EventTime = _clock.Now,
                DocumentTypeCode = documentTypeCode,
                FieldCount = fieldCount
            });
    }
}

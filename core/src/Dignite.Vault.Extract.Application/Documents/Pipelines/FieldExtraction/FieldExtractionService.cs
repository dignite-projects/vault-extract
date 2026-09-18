using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Review;
using Dignite.Vault.Extract.FlexFields;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction execution engine (#289 step 1). Extracts the core action that used to be inline in the
/// classification→field-extraction cascade into a reusable unit:
/// "read field definitions -> <see cref="FieldExtractionWorkflow.ExtractAsync"/> -> in-flight guard -> <c>Document.SetFlexFields</c>
/// -> publish <see cref="FieldsExtractedEto"/>", shared by two trigger types:
/// <list type="bullet">
///   <item>the classification-completed cascade: since #527 §8 the classification stage schedules this run
///   transactionally with classification completion (<c>DocumentPipelineJobScheduler</c>), forwarding the just-assigned
///   TypeCode as <c>ExpectedEventTypeCode</c> — the stale-reclassify early-exit hint this engine still guards on,
///   alongside its cross-tenant / in-flight guards;</item>
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
    private readonly IFieldRepository _fieldRepository;
    /// <summary>
    /// The query index is derived state, not a second source of truth, so it has to be re-derived in the
    /// same unit of work that writes the bag it comes from. Miss one write site and that document simply
    /// stops matching field filters — silently, because the bag itself is correct and every read that goes
    /// through the bag still shows the value.
    /// </summary>
    private readonly IFlexFieldIndexManager<Document> _indexManager;
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
    private readonly VaultExtractBehaviorOptions _behaviorOptions;
    private readonly ILogger<FieldExtractionService> _logger;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    public FieldExtractionService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldRepository fieldRepository,
        IFlexFieldIndexManager<Document> indexManager,
        ReviewStateEvaluator reviewEvaluator,
        FieldExtractionWorkflow workflow,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ICancellationTokenProvider cancellationTokenProvider,
        IOptions<VaultExtractBehaviorOptions> behaviorOptions,
        ILogger<FieldExtractionService> logger,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldRepository = fieldRepository;
        _indexManager = indexManager;
        _reviewEvaluator = reviewEvaluator;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _cancellationTokenProvider = cancellationTokenProvider;
        _behaviorOptions = behaviorOptions.Value;
        _logger = logger;
        _fieldTypeExtensionRegistry = fieldTypeExtensionRegistry;
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
            List<Field> definitions;
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

                definitions = await _fieldRepository.GetListAsync(documentTypeId);
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
                // #491: a type with no fields issues no LLM call, so an oversized body can no longer hold this document
                // back — clear FieldExtractionIncomplete, otherwise reclassifying a declined document to a field-less
                // type would leave it blocked from Ready forever with nothing an operator could do about it.
                var hadFields = blankDocument.FlexFields.Count > 0;
                var hadMissingRequired =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.MissingRequiredFields) != DocumentReviewReasons.None;
                var hadFingerprint = blankDocument.FieldFingerprint != null;
                var hadDuplicateSuspected =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None;
                var hadExtractionIncomplete =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.FieldExtractionIncomplete) != DocumentReviewReasons.None;
                // #527 §6: the no-field-definition path also clears any residual validation warnings + the coupled bit.
                var hadValidationWarnings = blankDocument.FieldValidationWarnings.Count > 0;
                if (hadFields || hadMissingRequired || hadFingerprint || hadDuplicateSuspected || hadExtractionIncomplete || hadValidationWarnings)
                {
                    blankDocument.SetFlexFields(null);
                    blankDocument.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
                    blankDocument.SetFieldFingerprint(null);
                    blankDocument.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: false);
                    blankDocument.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: false);
                    blankDocument.ReplaceFieldValidationWarnings(null);
                    await _documentRepository.UpdateAsync(blankDocument, autoSave: true);
                    await _indexManager.SynchronizeAsync(blankDocument);
                }

                await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount: 0, documentTypeCode);
                await clearUow.CompleteAsync();
                return FieldExtractionResult.Cleared;
            }

            var descriptors = definitions.Select(d => new FieldExtractionDescriptor(
                d.Id, d.Name, d.Description, d.FieldTypeName, d.Configuration, d.IsRequired)).ToList();

            // #491: field extraction sends the WHOLE Markdown (a type-bound field can sit anywhere, so tail truncation
            // would silently miss it — see FieldExtractionWorkflow). An unbounded body is therefore an unbounded
            // prompt-token cost and, after PromptBoundary.Encode + WrapDocument + request serialization, several times
            // its own size live on the LOH. Above the ceiling, decline the call entirely — the same trade
            // DocumentSegmentationJob makes for the same reason, and the same reason it is a gate rather than a
            // truncation. This path is reachable only when the type declares field definitions (the definitions.Count
            // == 0 branch returned above), so the review signal always means "fields were expected and we declined to
            // look", never "this type has no fields".
            if (markdown.Length > _behaviorOptions.MaxFieldExtractionMarkdownLength)
            {
                return await DeclineOversizedAsync(documentId, tenantId, documentTypeId, markdown.Length);
            }

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

            // #527 §1: one LLM call returns both the field values and the field validation warnings.
            var extractionResult = await _workflow.ExtractAsync(descriptors, markdown, _cancellationTokenProvider.Token);
            var extracted = extractionResult.Values;

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
            var currentDefinitions = await _fieldRepository.GetListAsync(documentTypeId);
            var currentDefinitionsById = currentDefinitions.ToDictionary(d => d.Id);

            var fieldValues = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var d in descriptors)
            {
                if (!extracted.TryGetValue(d.Name, out var value) || !value.HasValue)
                {
                    continue;
                }

                if (!currentDefinitionsById.TryGetValue(d.FieldId, out var currentDefinition))
                {
                    _logger.LogInformation(
                        "Field {FieldId} was removed or disabled during extraction for doc {DocumentId}; extracted value skipped.",
                        d.FieldId, documentId);
                    continue;
                }

                // The in-flight guard now compares the field TYPE, which is what v2 needed two checks for:
                // DataType covered "what shape is this value" and AllowMultiple covered "is it a list", and
                // under v3 both follow from FieldTypeName. A configuration change (say, options removed from
                // a Select) needs no guard of its own, because the reader below validates against the
                // definition as it stands NOW, not as the descriptor captured it before the call.
                if (!string.Equals(currentDefinition.FieldTypeName, d.FieldTypeName, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Field {FieldId} changed type during extraction for doc {DocumentId}: {OldFieldType} -> {NewFieldType}; stale value skipped.",
                        d.FieldId, documentId, d.FieldTypeName, currentDefinition.FieldTypeName);
                    continue;
                }

                // The name is the bag's key, so a rename mid-call would file the value under a key nothing
                // reads. The value belongs to the field it was extracted for, and that field now lives under
                // a different key - so it is stale in exactly the sense the other guards mean.
                if (!string.Equals(currentDefinition.Name, d.Name, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Field {FieldId} was renamed during extraction for doc {DocumentId}: {OldName} -> {NewName}; stale value skipped.",
                        d.FieldId, documentId, d.Name, currentDefinition.Name);
                    continue;
                }

                // Validated against the CURRENT definition and converted in one step. A value the model
                // returned in the wrong shape, or outside a Select's options, is skipped and logged rather
                // than stored - the same "store nothing, say so" trade v2 made, now with one gate instead
                // of a validator here and a converter in the entity.
                if (!FlexFieldValueReader.TryRead(
                        value.Value, currentDefinition.FieldTypeName, currentDefinition.Configuration,
                        _fieldTypeExtensionRegistry, out var read))
                {
                    _logger.LogWarning(
                        "FieldExtractionWorkflow returned a value for field {FieldName} ({FieldId}) on doc {DocumentId} that does not match field type {FieldType} (JSON kind {JsonValueKind}); value skipped.",
                        currentDefinition.Name, currentDefinition.Id, documentId,
                        currentDefinition.FieldTypeName, value.Value.ValueKind);
                    continue;
                }

                if (read != null)
                {
                    fieldValues[currentDefinition.Name] = read;
                }
            }

            document.SetFlexFields(fieldValues);

            // #527 §5/§7: persist the field validation warnings atomically with SetFlexFields and the FieldsExtractedEto
            // below. The workflow keys warnings by field name and has already normalized them (§3); here each is resolved
            // to the immutable FieldDefinitionId and passed through the SAME in-flight guards as the values — a warning
            // whose field was deleted, renamed, or changed shape while the LLM was in flight is discarded, never creating
            // stale review state. ReplaceFieldValidationWarnings couples the blocking FieldValidationWarning bit to the
            // collection, so a clean re-extraction (no warnings) clears both and lets the document re-derive to Ready.
            var descriptorsByName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);
            var warnings = new List<FieldValidationWarning>();
            foreach (var w in extractionResult.ValidationWarnings)
            {
                // The same in-flight guards the values pass through, so a warning whose field was removed,
                // renamed, or retyped mid-call is discarded rather than left as stale review state.
                if (!descriptorsByName.TryGetValue(w.FieldName, out var descriptor)
                    || !currentDefinitionsById.TryGetValue(descriptor.FieldId, out var currentDefinition)
                    || !string.Equals(currentDefinition.FieldTypeName, descriptor.FieldTypeName, StringComparison.Ordinal)
                    || !string.Equals(currentDefinition.Name, descriptor.Name, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Field validation warning for '{FieldName}' on doc {DocumentId} was discarded: its field was removed, renamed, or changed shape during extraction.",
                        w.FieldName, documentId);
                    continue;
                }

                warnings.Add(new FieldValidationWarning(currentDefinition.Id, w.Message));
            }

            document.ReplaceFieldValidationWarnings(warnings);

            // #491: this run reached the LLM, so an earlier declined run has been superseded. The only way that happens
            // is the host raising MaxFieldExtractionMarkdownLength — Markdown is write-once (SetMarkdown immutability),
            // so a document's body can never shrink in place.
            document.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: false);

            // #284: evaluate required-field missingness at the moment extraction completes and materialize MissingRequiredFields.
            // Read paths cannot know whether extraction already ran, so this must be decided at write time.
            // Reuse currentDefinitions reread before writing, filtered by IsRequired, plus the keys now present in the value bag.
            // This is after the stale guard, so stale events cannot reach here and no new race is introduced.
            // Non-blocking: setting MRF does not move an already Ready document back, because DeriveLifecycle checks only blocking reasons.
            var requiredIds = currentDefinitions.Where(cd => cd.IsRequired).Select(cd => cd.Id).ToList();
            var extractedIds = currentDefinitions.Where(cd => document.FlexFields.ContainsKey(cd.Name)).Select(cd => cd.Id).ToList();
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
            var fingerprint = FlexFieldFingerprintCalculator.Compute(document, currentDefinitions, _fieldTypeExtensionRegistry);
            document.SetFieldFingerprint(fingerprint);

            var duplicateSuspected = false;
            if (fingerprint != null && !document.DuplicateAllowed)
            {
                var candidates = await _documentRepository.FindDuplicateCandidatesAsync(
                    document.Id,
                    documentTypeId,
                    fingerprint,
                    DocumentConsts.MaxDuplicateCandidates,
                    // #635: unrestricted, explicitly. This runs in a background job with no principal and only
                    // counts the candidates to decide the flag — a duplicate the uploader may not see is still a
                    // duplicate, and narrowing here would make the review reason depend on who happened to upload
                    // the other copy. The operator-facing panel narrows instead (DocumentAppService).
                    DocumentAccessScope.Unrestricted,
                    _cancellationTokenProvider.Token);
                duplicateSuspected = candidates.Count > 0;
            }

            document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, duplicateSuspected);

            // FieldsExtractedEto.FieldCount is the logical field count - fields that got a value. Each bag
            // entry is one field, so this is simply the count; under v2 the same number needed a Distinct()
            // over rows, because a multi-value field expanded into several of them.
            var fieldCount = fieldValues.Count;

            await _documentRepository.UpdateAsync(document, autoSave: true);
            await _indexManager.SynchronizeAsync(document);
            await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount, documentTypeCode);

            await writeUow.CompleteAsync();

            _logger.LogInformation(
                "Field extraction for document {DocumentId} produced {NonNullCount}/{TotalCount} non-null fields.",
                documentId, fieldCount, definitions.Count);

            return FieldExtractionResult.Extracted(fieldCount);
        }
    }

    /// <summary>
    /// #491: the document's Markdown is over <c>MaxFieldExtractionMarkdownLength</c>, so no LLM call is issued. Records the
    /// blocking <see cref="DocumentReviewReasons.FieldExtractionIncomplete"/> signal in a short UoW and returns a
    /// <b>terminal</b> outcome, so <c>DocumentFieldExtractionBackgroundJob</c> completes the run instead of rethrowing into
    /// the job-store retry loop (which would keep re-sending the same oversized body).
    /// <para>
    /// Existing field values are deliberately left untouched. A host lowering the ceiling must not silently
    /// delete values that an earlier, in-budget extraction had legitimately produced; the blocking signal already tells
    /// downstream and the operator that this document's field set is not current. Nothing is published either — no
    /// extraction happened, so there is no <c>FieldsExtractedEto</c> to fire.
    /// </para>
    /// </summary>
    protected virtual async Task<FieldExtractionResult> DeclineOversizedAsync(
        Guid documentId,
        Guid? tenantId,
        Guid documentTypeId,
        int markdownLength)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.FindAsync(documentId, includeDetails: false);
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

        // Same stale-reclassify guard as the write path: the type captured in phase 1 must still be the current one,
        // otherwise a stale run would pin a blocking signal onto a document that has since moved to another type.
        if (document.DocumentTypeId != documentTypeId)
        {
            _logger.LogInformation(
                "Reclassified before the oversized-document decline could be recorded: captured typeId={CapturedTypeId} " +
                "current typeId={DocTypeId} doc={DocumentId}. Discarding.",
                documentTypeId, document.DocumentTypeId, documentId);
            return FieldExtractionResult.Skipped;
        }

        document.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        await uow.CompleteAsync();

        _logger.LogWarning(
            "Field extraction declined for document {DocumentId}: Markdown is {CharCount} characters, over the " +
            "MaxFieldExtractionMarkdownLength ceiling of {Ceiling}. No LLM call was made; the document carries the " +
            "blocking FieldExtractionIncomplete review reason and is withheld from Ready.",
            documentId, markdownLength, _behaviorOptions.MaxFieldExtractionMarkdownLength);

        return FieldExtractionResult.Declined;
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

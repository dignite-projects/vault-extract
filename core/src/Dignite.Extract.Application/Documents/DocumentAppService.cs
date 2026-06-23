using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Dignite.Extract.Documents;
using Dignite.Extract.Documents.Pipelines;
using Dignite.Extract.Documents.Review;
using Dignite.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Extract.Documents;

public class DocumentAppService : ExtractAppService, IDocumentAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IBlobContainer<ExtractDocumentContainer> _blobContainer;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly ReviewStateEvaluator _reviewEvaluator;

    public DocumentAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ICabinetRepository cabinetRepository,
        IBlobContainer<ExtractDocumentContainer> blobContainer,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus,
        ReviewStateEvaluator reviewEvaluator)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _cabinetRepository = cabinetRepository;
        _blobContainer = blobContainer;
        _pipelineRunManager = pipelineRunManager;
        _pipelineJobScheduler = pipelineJobScheduler;
        _distributedEventBus = distributedEventBus;
        _reviewEvaluator = reviewEvaluator;
    }

    public virtual async Task<DocumentDto> GetAsync(Guid id)
    {
        // Programmatic authorization assertion inside the method body. This is also the authorization guard for MCP exports
        // because DocumentResources delegates to this method (#222). MCP / reflection / tool-dispatch paths do not pass through HTTP [Authorize],
        // so this assertion must not be rewritten as a class-level or method-level [Authorize] attribute.
        await CheckPolicyAsync(ExtractPermissions.Documents.Default);
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        return await MapToDtoAsync(document);
    }

    public virtual async Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input)
    {
        await CheckPolicyAsync(ExtractPermissions.Documents.Default);

        // Resolve external type code -> internal DocumentTypeId (#207). If a type code is supplied but the layer has no such type:
        // with field filters -> loud fail because fields cannot be resolved; metadata-only -> empty page because no documents have that type.
        Guid? documentTypeId = null;
        if (!input.DocumentTypeCode.IsNullOrWhiteSpace())
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync(input.DocumentTypeCode!);
            if (type == null)
            {
                if (input.FieldFilters is { Count: > 0 })
                {
                    throw new BusinessException(ExtractErrorCodes.ExtractedField.Unknown)
                        .WithData("FieldName", input.FieldFilters[0].Name ?? string.Empty)
                        .WithData("DocumentTypeCode", input.DocumentTypeCode!);
                }
                return new PagedResultDto<DocumentListItemDto>(0, new List<DocumentListItemDto>());
            }
            documentTypeId = type.Id;
        }

        // ExtractedFields value filters: resolve each FieldFilter into a DocumentFieldQuery carrying FieldDefinitionId + declared type.
        // FieldDefinition cross-aggregate lookup is the caller-layer responsibility. If any field is not defined under that type, loud fail
        // with UnknownExtractedField as a correctable signal, instead of silently returning empty. No FieldFilters -> null (metadata-only retrieval).
        var fieldQueries = await ResolveFieldQueriesAsync(input, documentTypeId);

        // Trash-bin view: requires Restore permission, and the entire query pipeline must run inside DataFilter.Disable<ISoftDelete>.
        if (input.IsDeleted == true)
        {
            await CheckPolicyAsync(ExtractPermissions.Documents.Restore);
            using (DataFilter.Disable<ISoftDelete>())
            {
                return await ExecuteListQueryAsync(input, documentTypeId, onlyDeleted: true, fieldQueries);
            }
        }

        return await ExecuteListQueryAsync(input, documentTypeId, onlyDeleted: false, fieldQueries);
    }

    protected virtual async Task<List<DocumentFieldQuery>?> ResolveFieldQueriesAsync(
        GetDocumentListInput input, Guid? documentTypeId)
    {
        if (input.FieldFilters is not { Count: > 0 })
        {
            return null;
        }

        // DTO validation already guarantees DocumentTypeCode is non-empty when FieldFilters exist; documentTypeId was resolved above and is non-null, otherwise we already threw or returned.
        var fieldQueries = new List<DocumentFieldQuery>(input.FieldFilters.Count);
        foreach (var filter in input.FieldFilters)
        {
            var definition = await _fieldDefinitionRepository.FindByNameAsync(documentTypeId!.Value, filter.Name!);
            if (definition == null)
            {
                throw new BusinessException(ExtractErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", filter.Name!)
                    .WithData("DocumentTypeCode", input.DocumentTypeCode!);
            }

            // Internally match child rows by FieldDefinitionId (#207); FieldName is only for repository error diagnostics.
            fieldQueries.Add(new DocumentFieldQuery(
                definition.Id, filter.Name!, definition.DataType, filter.Value, filter.Min, filter.Max));
        }

        return fieldQueries;
    }

    protected virtual async Task<PagedResultDto<DocumentListItemDto>> ExecuteListQueryAsync(
        GetDocumentListInput input,
        Guid? documentTypeId,
        bool onlyDeleted,
        List<DocumentFieldQuery>? fieldQueries)
    {
        // Use WithDetailsAsync(selector) to eager-load only ExtractedFieldValues child rows, excluding PipelineRuns.
        // This is persistence-agnostic through the ABP repository API and avoids direct EF Core .Include dependency in the App layer.
        // A single JOIN retrieves the data for assembling the ExtractedFields dictionary, preventing per-document N+1 / lazy loading
        // (Issue #206 review guardrail).
        var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);

        // ExtractedFields value filtering: the repository uses Documents-anchored LINQ (child EXISTS + typed-column comparison)
        // to get the matching Id set anchored to DocumentTypeId, then intersects it with this query. This keeps ApplyFilter
        // as the single source for metadata filtering.
        if (fieldQueries is { Count: > 0 })
        {
            var matchedIds = await _documentRepository.GetFieldMatchedIdsAsync(documentTypeId!.Value, fieldQueries);
            query = query.Where(d => matchedIds.Contains(d.Id));
        }

        query = ApplyFilter(query, input, documentTypeId);
        if (onlyDeleted)
        {
            query = query.Where(d => d.IsDeleted);
        }

        var totalCount = await AsyncExecuter.CountAsync(query);

        query = ApplySorting(query, input.Sorting);
        query = query.Skip(input.SkipCount).Take(input.MaxResultCount);

        var documents = await AsyncExecuter.ToListAsync(query);
        var dtos = ObjectMapper.Map<List<Document>, List<DocumentListItemDto>>(documents);

        // DocumentTypeCode + ExtractedFields dictionary keys are Id -> code/name projections: populate them after pagination with one batch join, no N+1.
        await FillListReferencesAsync(documents, dtos);

        return new PagedResultDto<DocumentListItemDto>(totalCount, dtos);
    }

    [Authorize(ExtractPermissions.Documents.Upload)]
    public virtual async Task<DocumentDto> UploadAsync(UploadDocumentInput input)
    {
        // Pre-check: the current layer must have at least one DocumentType (CLAUDE.md "two-layer document type system", exact single-layer match).
        // Host startup seeding entry points were removed (HostDocumentTypeDataSeedContributor / DocumentTypeOptions).
        // DocumentTypes can now only be created at runtime through IDocumentTypeAppService, so a new deployment / tenant must create types before upload.
        // Without this fail-fast check, upload would succeed, classification candidates would be empty, and the document would stay in the manual-review queue forever.
        var hasType = await _documentTypeRepository.GetCountAsync() > 0;
        if (!hasType)
        {
            throw new BusinessException(ExtractErrorCodes.DocumentType.NoneConfigured);
        }

        // Cabinet ownership validation (#194): when cabinetId is specified, assert Cabinets permission first
        // (fail-closed, symmetric with the frontend canViewCabinets gate). [Authorize(Documents.Upload)] does not cover cabinet ownership;
        // without this assertion, a user without Cabinets permission could bypass the UI and assign a document to a hidden cabinet.
        // Then validate cabinet existence. Tenant isolation is enforced by the ambient IMultiTenant filter, so cross-tenant FindAsync returns null.
        // Cabinets are orthogonal to pipelines; this only validates manual ownership during upload, and later pipelines do not touch it.
        if (input.CabinetId.HasValue)
        {
            await CheckPolicyAsync(ExtractPermissions.Cabinets.Default);

            var cabinet = await _cabinetRepository.FindAsync(input.CabinetId.Value);
            if (cabinet == null)
            {
                throw new BusinessException(ExtractErrorCodes.Cabinet.InvalidId)
                    .WithData("CabinetId", input.CabinetId.Value);
            }
        }

        var fileName = input.File.FileName ?? "document";
        var contentType = input.File.ContentType ?? "application/octet-stream";
        var extension = Path.GetExtension(fileName);

        // Fail-closed file validation (#221): dual allow-list for content-type + extension. Any accepted file is stored as a blob
        // and triggers text extraction / classification jobs, consuming compute and creating uncertain behavior for unsupported formats.
        // Therefore unsupported channel formats loud-fail immediately: no blob write and no enqueue.
        // Content-type is client-spoofable, while extension determines blob suffix + DefaultTextExtractor dispatch, so both are validated.
        if (string.IsNullOrEmpty(extension) ||
            !DocumentConsts.AllowedUploadExtensions.Contains(extension) ||
            !DocumentConsts.AllowedUploadContentTypes.Contains(contentType))
        {
            throw new BusinessException(ExtractErrorCodes.Document.UnsupportedFileType)
                .WithData("FileName", fileName)
                .WithData("ContentType", contentType);
        }

        // The client-declared length gets a cheap early rejection first, but it is untrusted: attackers can underreport or omit it.
        // The real boundary is the hard limit enforced by the streaming copy below using actual bytes read. Over-limit bodies abort immediately
        // without buffering the full oversized body into memory.
        if (input.File.ContentLength is > 0 and var declared && declared > DocumentConsts.MaxUploadFileBytes)
        {
            throw new BusinessException(ExtractErrorCodes.Document.FileTooLarge)
                .WithData("FileName", fileName)
                .WithData("MaxBytes", DocumentConsts.MaxUploadFileBytes);
        }

        // Buffer inside an independent using scope: release the buffer immediately after bytes are obtained, so SaveAsync retains only bytes (1x).
        // This removes the earlier buffer + bytes simultaneous residency (~2x memory amplification, #221 review follow-up 1).
        // Hashing requires the full byte array, so this cannot be fully streamed.
        byte[] bytes;
        await using (var source = input.File.GetStream())
        using (var buffer = new MemoryStream())
        {
            await CopyWithLimitAsync(source, buffer, DocumentConsts.MaxUploadFileBytes, fileName);
            bytes = buffer.ToArray();
        }
        var fileSize = bytes.LongLength;

        var contentHash = ContentHasher.Sha256Hex(bytes);

        // Content-hash deduplication is check-then-act because ContentHash has only a non-unique index. Two concurrent uploads of the same file
        // can both pass the check and create duplicate Documents. This race is intentionally accepted for now (#221 review follow-up 2):
        // low probability and low impact (at most one duplicate, removable later). Adding a unique index to ContentHash would turn the losing side
        // of the race into a 500; the channel layer does not pay that cost for low-probability duplication.
        var existing = await _documentRepository.FindByContentHashAsync(contentHash);
        if (existing != null)
        {
            var errorCode = existing.IsDeleted
                ? ExtractErrorCodes.Document.InRecycleBin
                : ExtractErrorCodes.Document.Duplicate;

            throw new BusinessException(errorCode)
                .WithData("FileName", fileName)
                .WithData("ExistingDocumentId", existing.Id);
        }

        var blobName = GuidGenerator.Create().ToString("N") + extension;
        using (var saveStream = new MemoryStream(bytes, writable: false))
        {
            await _blobContainer.SaveAsync(blobName, saveStream);
        }

        var fileOrigin = new FileOrigin(
            blobName,
            CurrentUser.UserName ?? string.Empty,
            contentType,
            contentHash,
            fileSize,
            originalFileName: fileName);

        var document = new Document(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            fileOrigin,
            cabinetId: input.CabinetId);

        await _documentRepository.InsertAsync(document, autoSave: true);

        await _distributedEventBus.PublishAsync(
            new DocumentUploadedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                FileName = fileName,
                FileSize = fileSize,
                ContentType = contentType
            });

        await _pipelineJobScheduler.QueueAsync(document, ExtractPipelines.Parse);

        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> while enforcing a hard limit by actual bytes read (#221).
    /// Throws <c>Document.FileTooLarge</c> as soon as the cumulative count exceeds <paramref name="maxBytes"/>.
    /// Does not rely on client-declared ContentLength, which can be forged, and does not buffer the entire oversized body into memory.
    /// </summary>
    protected static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, string fileName)
    {
        var rented = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(rented)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new BusinessException(ExtractErrorCodes.Document.FileTooLarge)
                    .WithData("FileName", fileName)
                    .WithData("MaxBytes", maxBytes);
            }

            await destination.WriteAsync(rented.AsMemory(0, read));
        }
    }

    public virtual async Task<IRemoteStreamContent> GetBlobAsync(Guid id)
    {
        await CheckPolicyAsync(ExtractPermissions.Documents.Default);

        // Only fetch the blob stream: scalar fields + owned FileOrigin are loaded with the entity, and no child collection is needed.
        var document = await _documentRepository.GetAsync(id, includeDetails: false);

        if (document.FileOrigin is null)
            throw new BusinessException(ExtractErrorCodes.Document.NoSourceBlob);

        var stream = await _blobContainer.GetAsync(document.FileOrigin.BlobName);

        return new RemoteStreamContent(
            stream,
            document.FileOrigin.OriginalFileName,
            document.FileOrigin.ContentType,
            disposeStream: true);
    }

    [Authorize(ExtractPermissions.Documents.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id);

        // A source document must not enter the recycle bin while it still has live derived sub-documents (#306 / #346):
        // soft-deleting it would strand its constituents — their OriginDocumentId provenance back-reference would dangle
        // and their detail page could no longer resolve the now-gone source. Block the delete and let the operator remove
        // its sub-documents first (the list's "view sub-documents" filter surfaces them by OriginDocumentId). Children
        // already in the recycle bin do not count (the ambient ISoftDelete filter excludes them), so a source whose
        // sub-documents are all already deleted can still be deleted. This guard is intentionally NOT a cascade: deleting
        // the parent never auto-deletes children (they are independent peers that outlive the source, see
        // Document.CreateDerived), unlike the #349 container→type reclassify retraction.
        if (await _documentRepository.AnyByOriginAsync(id))
        {
            throw new BusinessException(ExtractErrorCodes.Document.HasSubDocuments)
                .WithData("DocumentId", id);
        }

        await _documentRepository.DeleteAsync(id);

        // Notify downstream consumers: the Document entered the trash bin, so derived data should move to a recoverable archived state.
        await _distributedEventBus.PublishAsync(
            new DocumentDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now
            });
    }

    [Authorize(ExtractPermissions.Documents.PermanentDelete)]
    public virtual async Task PermanentDeleteAsync(Guid id)
    {
        Document document;
        using (DataFilter.Disable<ISoftDelete>())
        {
            // Permanent delete needs only scalar fields + owned FileOrigin (blob name); no child collections are needed.
            document = await _documentRepository.GetAsync(id, includeDetails: false);
        }

        await _documentRepository.HardDeleteAsync(id);

        if (document.FileOrigin is not null)
        {
            try
            {
                await _blobContainer.DeleteAsync(document.FileOrigin.BlobName);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Failed to delete blob {BlobName} for document {DocumentId}.",
                    document.FileOrigin.BlobName, id);
            }
        }

        // #210: permanently delete archived native payload blobs together, using stable keys from the manifest and no prefix cleanup.
        // Like the original file blob, this is best-effort: failures are logged but do not block the main permanent-delete flow.
        var nativePayloadBlobName = document.ExtractionMetadata?.NativePayloadManifest?.BlobName;
        if (!string.IsNullOrEmpty(nativePayloadBlobName))
        {
            try
            {
                await _blobContainer.DeleteAsync(nativePayloadBlobName);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Failed to delete native payload blob {BlobName} for document {DocumentId}.",
                    nativePayloadBlobName, id);
            }
        }

        // Notify downstream consumers: the Document is unrecoverable, so derived data should be physically deleted.
        await _distributedEventBus.PublishAsync(
            new DocumentPermanentlyDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now
            });
    }

    [Authorize(ExtractPermissions.Documents.Restore)]
    public virtual async Task RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var document = await _documentRepository.GetAsync(id);
            if (!document.IsDeleted)
            {
                return;
            }

            document.IsDeleted = false;
            document.DeletionTime = null;
            document.DeleterId = null;

            await _documentRepository.UpdateAsync(document);

            await _distributedEventBus.PublishAsync(
                new DocumentRestoredEto
                {
                    DocumentId = document.Id,
                    TenantId = document.TenantId,
                    EventTime = Clock.Now
                });
        }
    }

    /// <summary>
    /// Retries one pipeline. Currently only <see cref="PipelineRunStatus.Failed"/> can be retried;
    /// Pending/Running throw <c>PipelineRetryInProgress</c>, and Succeeded/Skipped throw <c>PipelineNotRetryable</c>.
    /// Retry first creates a Pending Run, then enqueues a BackgroundJob carrying PipelineRunId.
    /// Chained replay semantics are implicit: retrying <c>text-extraction</c> triggers <c>classification</c> after success.
    /// </summary>
    [Authorize(ExtractPermissions.Documents.Pipelines.Retry)]
    public virtual async Task RetryPipelineAsync(Guid id, RetryPipelineInput input)
    {
        if (!ExtractPipelines.RetryablePipelines.Contains(input.PipelineCode))
        {
            throw new BusinessException(ExtractErrorCodes.Pipeline.UnknownCode)
                .WithData("PipelineCode", input.PipelineCode);
        }

        // Need only the main document row (IsDeleted / FileOrigin) + latest run for this pipeline to decide retryability; field values are not touched.
        // Tenant isolation is enforced by the ambient IMultiTenant filter; GetAsync throws EntityNotFound for cross-tenant or missing id.
        // #216 follow-up: retry state-machine checks moved down to DocumentPipelineRunManager.EnsureRetryableAsync,
        // so AppService no longer directly depends on IDocumentPipelineRunRepository.
        var document = await _documentRepository.GetAsync(id, includeDetails: false);

        if (document.IsDeleted)
        {
            throw new BusinessException(ExtractErrorCodes.Document.InRecycleBin)
                .WithData("FileName", document.FileOrigin?.BlobName);
        }

        var latestRun = await _pipelineRunManager.EnsureRetryableAsync(id, input.PipelineCode);

        Logger.LogInformation(
            "RetryPipelineAsync user={UserId} tenant={TenantId} doc={DocumentId} pipeline={PipelineCode} previousAttempt={Attempt}",
            CurrentUser.Id, CurrentTenant.Id, document.Id, input.PipelineCode, latestRun.AttemptNumber);

        await _pipelineJobScheduler.QueueAsync(document, input.PipelineCode);
    }

    /// <summary>
    /// "Re-recognize" (#263): reruns AI automatic classification on existing Markdown -> cascades field re-extraction, without rerunning OCR.
    /// Re-enqueues the classification job on the same path used after text extraction completes. The background job performs LLM automatic reclassification;
    /// after completion, high confidence emits <see cref="DocumentClassifiedEto"/> to cascade field re-extraction, while low confidence enters manual review.
    /// <para>
    /// See <see cref="IDocumentAppService.RerecognizeAsync"/> for semantic boundaries with <see cref="ReclassifyAsync"/>
    /// (operator-specified type, synchronous persistence) and <see cref="RetryPipelineAsync"/> (only Failed runs are retryable).
    /// </para>
    /// </summary>
    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task RerecognizeAsync(Guid id)
    {
        // Need only scalar fields (IsDeleted / Markdown / FileOrigin); field values are not touched. Tenant isolation is enforced by the ambient IMultiTenant filter.
        var document = await _documentRepository.GetAsync(id, includeDetails: false);

        if (document.IsDeleted)
        {
            throw new BusinessException(ExtractErrorCodes.Document.InRecycleBin)
                .WithData("FileName", document.FileOrigin?.BlobName);
        }

        // Automatic classification input is Document.Markdown. If text extraction has not produced text yet, reclassification cannot run.
        if (string.IsNullOrEmpty(document.Markdown))
        {
            throw new BusinessException(ExtractErrorCodes.Document.NotTextExtracted);
        }

        // Concurrency guard: do not re-enqueue while classification is Pending/Running. New attempts do not collide with the unique index for Running, so this must be blocked explicitly.
        await _pipelineRunManager.EnsureNotInProgressAsync(id, ExtractPipelines.Classification);

        Logger.LogInformation(
            "RerecognizeAsync user={UserId} tenant={TenantId} doc={DocumentId}",
            CurrentUser.Id, CurrentTenant.Id, document.Id);

        // Re-enqueue automatic classification. QueueAsync creates a Pending run, derives LifecycleStatus -> Processing, and enqueues the background job.
        await _pipelineJobScheduler.QueueAsync(document, ExtractPipelines.Classification);
    }

    /// <summary>
    /// "Field re-extraction only" (#289 scenario 2, single-document version): reruns only the <c>field-extraction</c> pipeline on the existing classification,
    /// without reclassification or OCR. Reuses the same background job and shared extraction engine as bulk field re-extraction.
    /// #411: <c>field-extraction</c> is now a key pipeline, so re-extracting an already-Ready document bounces it Ready -&gt; Processing -&gt; Ready (re-firing DocumentReadyEto, absorbed downstream via EventTime), and a newly-detected duplicate parks it in the review queue instead of returning to Ready.
    /// </summary>
    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task ReextractFieldsAsync(Guid id)
    {
        // Need only scalar fields (IsDeleted / DocumentTypeId / Markdown); field values are not touched. Tenant isolation is enforced by the ambient IMultiTenant filter.
        var document = await _documentRepository.GetAsync(id, includeDetails: false);

        if (document.IsDeleted)
        {
            throw new BusinessException(ExtractErrorCodes.Document.InRecycleBin)
                .WithData("FileName", document.FileOrigin?.BlobName);
        }

        // Field extraction hangs off DocumentType; unclassified documents have nothing to extract against.
        if (!document.DocumentTypeId.HasValue)
        {
            throw new BusinessException(ExtractErrorCodes.Document.NotClassified);
        }

        // Field extraction input is Document.Markdown. If text extraction has not produced text yet, extraction cannot run.
        if (string.IsNullOrEmpty(document.Markdown))
        {
            throw new BusinessException(ExtractErrorCodes.Document.NotTextExtracted);
        }

        // Concurrency guard: do not re-enqueue while field-extraction is Pending/Running, avoiding double-click stacking.
        // New attempts do not collide with the unique index for Running, so this must be blocked explicitly.
        await _pipelineRunManager.EnsureNotInProgressAsync(id, ExtractPipelines.FieldExtraction);

        Logger.LogInformation(
            "ReextractFieldsAsync user={UserId} tenant={TenantId} doc={DocumentId}",
            CurrentUser.Id, CurrentTenant.Id, document.Id);

        // Create a Pending field-extraction run + enqueue the background job. Lifecycle-neutral, so LifecycleStatus is unchanged.
        await _pipelineJobScheduler.QueueAsync(document, ExtractPipelines.FieldExtraction);
    }

    /// <summary>
    /// Operator edits field extraction results (individual correction). Replaces ExtractedFields as a whole;
    /// keys must be field names defined under this document's layer and DocumentType. After completion, reuses FieldsExtractedEto
    /// to notify downstream consumers to synchronize.
    /// </summary>
    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input)
    {
        // Tenant isolation is enforced by the ambient IMultiTenant filter; GetAsync already throws EntityNotFound for cross-tenant ids.
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // Field definitions hang off DocumentType; unclassified documents have no basis for validating field names.
        if (!document.DocumentTypeId.HasValue)
        {
            throw new BusinessException(ExtractErrorCodes.Document.NotClassified);
        }

        // ETO still carries the DocumentTypeCode string, preserving the export contract. It is resolved from internal DocumentTypeId (#207).
        var documentTypeCode = await ResolveTypeCodeAsync(document.DocumentTypeId);

        // Validate that each key is a field name defined under this document's layer and DocumentType.
        // GetListAsync reads a single layer by ambient CurrentTenant.Id (already asserted == document.TenantId) and matches by internal DocumentTypeId.
        var definitions = await _fieldDefinitionRepository.GetListAsync(document.DocumentTypeId.Value);
        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var fields = input.Fields ?? new Dictionary<string, JsonElement>();

        // After validating each value against the declared type, expand into typed DocumentFieldValue instances
        // (FieldDefinitionId + DataType come from FieldDefinition). Validated values can be passed directly to the aggregate root,
        // no longer through an intermediate JSON dictionary; conversion from value type to aligned columns is centralized in DocumentExtractedField.
        // #212: JSON arrays for multi-value text fields are split by DocumentFieldValueFactory into multiple rows (Order 0,1,2...);
        // single-value fields produce one row (Order 0).
        var fieldValues = new List<DocumentFieldValue>(fields.Count);
        foreach (var (key, value) in fields)
        {
            if (!definitionsByName.TryGetValue(key, out var definition))
            {
                throw new BusinessException(ExtractErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty);
            }

            if (!ExtractedFieldValueValidator.IsValid(value, definition.DataType, definition.AllowMultiple))
            {
                throw new BusinessException(ExtractErrorCodes.ExtractedField.InvalidValue)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("DataType", definition.DataType.ToString())
                    .WithData("AllowMultiple", definition.AllowMultiple.ToString())
                    .WithData("JsonValueKind", value.ValueKind.ToString());
            }

            fieldValues.AddRange(DocumentFieldValueFactory.Expand(
                definition.Id, definition.DataType, definition.AllowMultiple, value));
        }

        // Whole-set replacement, consistent with FieldExtractionEventHandler: empty means clear all field rows.
        document.SetFields(fieldValues);

        // #284: after operator entry, reevaluate missing required fields. If filled, clear MissingRequiredFields to close the review-queue loop;
        // if still missing, keep it. Reuse loaded definitions filtered by IsRequired plus the written ExtractedFieldValues.
        var requiredIds = definitions.Where(d => d.IsRequired).Select(d => d.Id).ToList();
        var extractedIds = document.ExtractedFieldValues.Select(f => f.FieldDefinitionId).Distinct().ToList();
        document.SetReviewReason(
            DocumentReviewReasons.MissingRequiredFields,
            _reviewEvaluator.MissingRequiredFieldsPresent(requiredIds, extractedIds));

        await _documentRepository.UpdateAsync(document, autoSave: true);

        // FieldsExtractedEto.FieldCount is the logical field count (distinct fields that produced >= 1 value), not the expanded row count.
        // Use the same algorithm as FieldExtractionEventHandler so both write paths emit the same thin signal for the same final state.
        // Empty arrays for multi-value fields expand to 0 rows and do not count, avoiding divergence from the LLM path.
        // Downstream consumers are idempotent by (DocumentId, EventType, EventTime) and pull back the latest field values.
        var fieldCount = fieldValues.Select(v => v.FieldDefinitionId).Distinct().Count();
        await _distributedEventBus.PublishAsync(
            new FieldsExtractedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = documentTypeCode,
                FieldCount = fieldCount
            });

        return await MapToDtoAsync(document);
    }

    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeId);
    }

    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeId);
    }

    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        document.RejectReview(input.Reason);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// #411: operator decides a suspected duplicate is acceptable. Sets the durable <c>DuplicateAllowed</c> override
    /// and clears the blocking <see cref="DocumentReviewReasons.DuplicateSuspected"/> reason, then re-derives lifecycle
    /// so the document is released to Ready (emitting <c>DocumentReadyEto</c>) when no other blocking reason remains.
    /// Reuses the review-resolution permission (same as Confirm / Reclassify / Reject). The opposite resolution —
    /// confirming the duplicate — is the existing <see cref="DeleteAsync"/>.
    /// </summary>
    [Authorize(ExtractPermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> AllowDuplicateAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        document.AllowDuplicate();
        await _pipelineRunManager.ReDeriveLifecycleAsync(document);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// Reassigns the document's cabinet (#257). Symmetric with <see cref="UploadAsync"/> cabinet ownership validation:
    /// assigning to a cabinet asserts <see cref="ExtractPermissions.Cabinets.Default"/> and validates that the cabinet exists in the current layer
    /// (tenant isolation is enforced by the ambient IMultiTenant filter, so cross-tenant FindAsync returns null). Removing from a cabinet (CabinetId == null)
    /// only needs method-level <see cref="ExtractPermissions.Documents.Default"/>. Cabinets are orthogonal to pipelines, so this triggers no later Run and emits no export event.
    /// </summary>
    [Authorize(ExtractPermissions.Documents.Default)]
    public virtual async Task<DocumentDto> UpdateCabinetAsync(Guid id, UpdateDocumentCabinetInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        if (input.CabinetId.HasValue)
        {
            await CheckPolicyAsync(ExtractPermissions.Cabinets.Default);

            var cabinet = await _cabinetRepository.FindAsync(input.CabinetId.Value);
            if (cabinet == null)
            {
                throw new BusinessException(ExtractErrorCodes.Cabinet.InvalidId)
                    .WithData("CabinetId", input.CabinetId.Value);
            }
        }

        document.SetCabinet(input.CabinetId);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// Shared implementation for Confirm and Reclassify: resolves type by immutable DocumentTypeId, writes ReviewDisposition=Confirmed
    /// (clearing UnresolvedClassification), and publishes DocumentClassifiedEto projected back to the renamable TypeCode export contract
    /// so downstream consumers can rerun field extraction.
    /// </summary>
    protected virtual async Task<DocumentDto> ApplyManualClassificationAsync(Guid id, Guid documentTypeId)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // Type validation responsibility lives in AppService and no longer goes through manager-internal EnsureRegisteredTypeCodeAsync:
        // resolve by immutable Id (#207), with tenant isolation delegated to ABP IMultiTenant global filters for exact single-layer matching.
        // Missing type fails fast, avoiding writes of a type that business-module subscribers cannot recognize.
        var typeDef = await _documentTypeRepository.FindAsync(documentTypeId);
        if (typeDef == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), documentTypeId);
        }

        var run = await _pipelineRunManager.QueueAsync(document, ExtractPipelines.Classification);
        await _pipelineRunManager.BeginAsync(document, run);

        await _pipelineRunManager.CompleteManualClassificationAsync(document, run, typeDef);
        await _distributedEventBus.PublishAsync(
            new DocumentClassifiedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = typeDef.TypeCode,
                ClassificationConfidence = 1.0
            });

        await _documentRepository.UpdateAsync(document, autoSave: true);

        return await MapToDtoAsync(document);
    }

    protected virtual IQueryable<Document> ApplyFilter(
        IQueryable<Document> query, GetDocumentListInput input, Guid? documentTypeId)
    {
        if (input.LifecycleStatus.HasValue)
            query = query.Where(x => x.LifecycleStatus == input.LifecycleStatus.Value);

        // Type filtering uses the resolved internal DocumentTypeId (#207).
        if (documentTypeId.HasValue)
            query = query.Where(x => x.DocumentTypeId == documentTypeId.Value);

        if (input.CabinetId.HasValue)
            query = query.Where(x => x.CabinetId == input.CabinetId.Value);

        // Sub-document provenance filter (#354): list the documents derived from a given source (e.g. a
        // container's children). The IMultiTenant global filter still applies, so both ends stay tenant-scoped.
        if (input.OriginDocumentId.HasValue)
            query = query.Where(x => x.OriginDocumentId == input.OriginDocumentId.Value);

        // Filter by manual-review disposition phase (#284). Rejected documents have ReviewDisposition=Rejected and can be queried explicitly.
        if (input.ReviewDisposition.HasValue)
            query = query.Where(d => d.ReviewDisposition == input.ReviewDisposition.Value);

        // Operator review queue (#284): any unresolved review reason (unresolved classification + missing required fields, one queue) and not rejected.
        // Rejected documents have already been handled by the operator, so they are not in the work queue; they can still be queried separately by ReviewDisposition=Rejected.
        // Uses the canonical DocumentReviewQueries.RequiresAttention predicate, shared with the overview
        // needs-review statistic (#333) so the queue and the count never drift.
        if (input.HasReviewReasons == true)
            query = query.Where(DocumentReviewQueries.RequiresAttention);

        return query;
    }

    protected virtual IQueryable<Document> ApplySorting(IQueryable<Document> query, string? sorting)
    {
        return sorting?.Trim().ToLowerInvariant() switch
        {
            "creationtime" or "creationtime asc" => query.OrderBy(x => x.CreationTime),
            "creationtime desc" => query.OrderByDescending(x => x.CreationTime),
            _ => query.OrderByDescending(x => x.CreationTime)
        };
    }

    // ===== #207: Id -> external code/name projection. Internally store DocumentTypeId / FieldDefinitionId, while export DTOs still output code/name.
    // Traverse soft-delete so archived types / fields referenced by historical documents can still resolve. No snapshot fields are introduced;
    // renames transparently reflect current values. =====

    /// <summary>Maps one Document -> DocumentDto and fills DocumentTypeCode + ExtractedFields (Id -> code/name) + extraction integrity (#268).</summary>
    protected virtual async Task<DocumentDto> MapToDtoAsync(Document document)
    {
        var dto = ObjectMapper.Map<Document, DocumentDto>(document);
        var (typeCodes, fieldNames) = await ResolveReferenceMapsAsync(new[] { document });
        dto.DocumentTypeCode = ResolveTypeCode(document.DocumentTypeId, typeCodes);
        dto.ExtractedFields = AssembleExtractedFields(document.ExtractedFieldValues, fieldNames);
        // #268: expose extraction integrity quality signal, not provenance. Null metadata (historical / digital-native / not extracted) is treated as complete.
        dto.ExtractionIsComplete = document.ExtractionMetadata?.IsComplete ?? true;
        dto.ExtractionIncompleteReason = document.ExtractionMetadata?.IncompleteReason;
        // #284: review axis: derived RequiresReview + thick detail entries including missing required field names. Server computes; client only renders.
        // Unified predicate including disposition: rejected documents may retain objective reasons but do not count as "requires attention";
        // details are cleared too, avoiding contradictory "rejected + pending review" presentation.
        dto.RequiresReview = ReviewReasonPolicy.RequiresAttention(document.ReviewReasons, document.ReviewDisposition);
        dto.ReviewReasonDetails = await BuildReviewReasonDetailsAsync(document);
        return dto;
    }

    /// <summary>Batch-fills list DTO DocumentTypeCode + ExtractedFields, resolving both mapping tables once after pagination, with no N+1.</summary>
    protected virtual async Task FillListReferencesAsync(
        IReadOnlyList<Document> documents, IReadOnlyList<DocumentListItemDto> dtos)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var (typeCodes, fieldNames) = await ResolveReferenceMapsAsync(documents);
        for (var i = 0; i < documents.Count; i++)
        {
            dtos[i].DocumentTypeCode = ResolveTypeCode(documents[i].DocumentTypeId, typeCodes);
            dtos[i].ExtractedFields = AssembleExtractedFields(documents[i].ExtractedFieldValues, fieldNames);
            // #284: thin list: expose only RequiresReview for badges and do not assemble details. Details are for the detail page to avoid list N+1.
            dtos[i].RequiresReview = ReviewReasonPolicy.RequiresAttention(documents[i].ReviewReasons, documents[i].ReviewDisposition);
        }
    }

    /// <summary>
    /// Assembles structured review reason details (#284, detail page only: thick detail). Each set reason bit produces one item;
    /// IsBlocking is filled from policy, and MissingRequiredFields additionally computes missing required field DisplayName values.
    /// No unresolved reasons -> null.
    /// </summary>
    protected virtual async Task<List<ReviewReasonDetailDto>?> BuildReviewReasonDetailsAsync(Document document)
    {
        // Same predicate source as RequiresReview: no unresolved reasons / rejected (operator already handled) -> do not assemble details.
        if (!ReviewReasonPolicy.RequiresAttention(document.ReviewReasons, document.ReviewDisposition))
        {
            return null;
        }

        var details = new List<ReviewReasonDetailDto>();

        if ((document.ReviewReasons & DocumentReviewReasons.UnresolvedClassification) != DocumentReviewReasons.None)
        {
            details.Add(new ReviewReasonDetailDto
            {
                Reason = DocumentReviewReasons.UnresolvedClassification,
                IsBlocking = ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.UnresolvedClassification)
            });
        }

        if ((document.ReviewReasons & DocumentReviewReasons.MissingRequiredFields) != DocumentReviewReasons.None)
        {
            // The MRF bit and missing field names can briefly disagree (in-flight schema change / re-extraction not persisted yet).
            // Skip this detail when field names are empty, instead of rendering an empty "missing required: 0 items" shell.
            // The MRF flag itself is still authoritatively maintained by the field extraction phase.
            var missingFieldNames = await BuildMissingRequiredFieldNamesAsync(document);
            if (missingFieldNames.Count > 0)
            {
                details.Add(new ReviewReasonDetailDto
                {
                    Reason = DocumentReviewReasons.MissingRequiredFields,
                    IsBlocking = ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.MissingRequiredFields),
                    MissingFieldNames = missingFieldNames
                });
            }
        }

        // #346: a container whose born-digital segmentation could not complete carries this non-blocking reason.
        // Project it so the detail page shows WHY the container needs attention (the client localizes by the Reason
        // enum); otherwise RequiresReview would be true with no explanation. No extra data — the reason bit is enough.
        if ((document.ReviewReasons & DocumentReviewReasons.SegmentationIncomplete) != DocumentReviewReasons.None)
        {
            details.Add(new ReviewReasonDetailDto
            {
                Reason = DocumentReviewReasons.SegmentationIncomplete,
                IsBlocking = ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.SegmentationIncomplete)
            });
        }

        // #411: a suspected duplicate. Recompute the candidate document Ids on read (no separate storage) so the
        // operator can open them side by side before allowing or discarding. If the fingerprint no longer collides
        // (the colliding document was deleted in the meantime), the candidate list is empty but the reason — owned by
        // the field extraction stage — is still shown; the operator can Allow to release it.
        if ((document.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None)
        {
            details.Add(new ReviewReasonDetailDto
            {
                Reason = DocumentReviewReasons.DuplicateSuspected,
                IsBlocking = ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.DuplicateSuspected),
                DuplicateCandidateDocumentIds = await BuildDuplicateCandidateIdsAsync(document)
            });
        }

        // If all reason-bit details were skipped (for example MRF is the only reason but field names are temporarily empty),
        // return null rather than an empty array. This keeps "no details" semantics consistent with frontend reviewReasonDetails?.length checks.
        // RequiresReview is still determined independently by the upstream predicate.
        return details.Count > 0 ? details : null;
    }

    /// <summary>
    /// DisplayName values for missing required fields: current IsRequired definitions for this type that are absent from the extracted value set.
    /// <para>
    /// #284: this method intentionally does <b>not</b> reuse <see cref="ResolveReferenceMapsAsync"/>. They have opposite soft-delete semantics and different keys.
    /// <c>ResolveReferenceMaps</c> uses <c>Disable&lt;ISoftDelete&gt;</c> and looks up by <b>FieldDefinitionId of extracted values</b>
    /// so archived fields referenced by historical documents can still resolve to field names at export time, preventing orphaned values.
    /// This method looks for fields that are "currently still required but <b>missing</b>", so it must read only <b>active</b> definitions
    /// and query all definitions by <b>DocumentTypeId</b>; missing items are naturally absent from by-id maps.
    /// Soft-deleted fields are no longer required and must never be reported as pending entry.
    /// This method is called once for a single document on the detail page, not in lists and not as N+1, so there is no performance reason to merge it.
    /// </para>
    /// </summary>
    protected virtual async Task<List<string>> BuildMissingRequiredFieldNamesAsync(Document document)
    {
        if (!document.DocumentTypeId.HasValue)
        {
            return new List<string>();
        }

        var definitions = await _fieldDefinitionRepository.GetListAsync(document.DocumentTypeId.Value);
        var extractedIds = document.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ToHashSet();
        return definitions
            .Where(d => d.IsRequired && !extractedIds.Contains(d.Id))
            .Select(d => d.DisplayName)
            .ToList();
    }

    /// <summary>
    /// #411: recomputes the duplicate-candidate document Ids for the detail page — other documents in the same layer +
    /// type sharing this document's <see cref="Document.FieldFingerprint"/>. Computed on read (no separate storage),
    /// hard-capped by <see cref="DocumentConsts.MaxDuplicateCandidates"/>, and tenant-/soft-delete-isolated by the
    /// repository's ambient global filters. Returns empty when there is no fingerprint (defensive: a set
    /// DuplicateSuspected reason normally implies one).
    /// </summary>
    protected virtual async Task<List<Guid>> BuildDuplicateCandidateIdsAsync(Document document)
    {
        if (document.FieldFingerprint == null || !document.DocumentTypeId.HasValue)
        {
            return new List<Guid>();
        }

        return await _documentRepository.FindDuplicateCandidateIdsAsync(
            document.Id,
            document.DocumentTypeId.Value,
            document.FieldFingerprint,
            DocumentConsts.MaxDuplicateCandidates);
    }

    /// <summary>
    /// Resolves all DocumentTypeId -> TypeCode and FieldDefinitionId -> (Name, DataType, AllowMultiple) mappings for this document batch at once.
    /// Traverses soft-delete so archived types / fields still resolve; IMultiTenant still isolates by ambient tenant because this batch belongs to one layer.
    /// DataType is loaded with Name (#208: field type is determined by FieldDefinition and is not persisted on field value rows) so
    /// <see cref="DocumentExtractedField.ToJsonElement"/> can reconstruct export JSON. AllowMultiple (#212) decides whether the field renders as a JSON array
    /// (multi-value) or scalar (single-value) in exports.
    /// </summary>
    protected virtual async Task<(Dictionary<Guid, string> TypeCodes, Dictionary<Guid, (string Name, FieldDataType DataType, bool AllowMultiple)> Fields)>
        ResolveReferenceMapsAsync(IReadOnlyCollection<Document> documents)
    {
        var typeIds = documents
            .Where(d => d.DocumentTypeId.HasValue)
            .Select(d => d.DocumentTypeId!.Value)
            .Distinct()
            .ToList();
        var fieldIds = documents
            .SelectMany(d => d.ExtractedFieldValues)
            .Select(f => f.FieldDefinitionId)
            .Distinct()
            .ToList();

        var typeCodes = new Dictionary<Guid, string>();
        var fields = new Dictionary<Guid, (string Name, FieldDataType DataType, bool AllowMultiple)>();

        using (DataFilter.Disable<ISoftDelete>())
        {
            if (typeIds.Count > 0)
            {
                foreach (var t in await _documentTypeRepository.GetListAsync(t => typeIds.Contains(t.Id)))
                {
                    typeCodes[t.Id] = t.TypeCode;
                }
            }

            if (fieldIds.Count > 0)
            {
                foreach (var f in await _fieldDefinitionRepository.GetListAsync(f => fieldIds.Contains(f.Id)))
                {
                    fields[f.Id] = (f.Name, f.DataType, f.AllowMultiple);
                }
            }
        }

        return (typeCodes, fields);
    }

    protected virtual string? ResolveTypeCode(Guid? documentTypeId, IReadOnlyDictionary<Guid, string> typeCodes)
        => documentTypeId.HasValue && typeCodes.TryGetValue(documentTypeId.Value, out var code) ? code : null;

    protected virtual Dictionary<string, JsonElement>? AssembleExtractedFields(
        IReadOnlyCollection<DocumentExtractedField> values,
        IReadOnlyDictionary<Guid, (string Name, FieldDataType DataType, bool AllowMultiple)> fieldDefs)
    {
        if (values.Count == 0)
        {
            return null;
        }

        // Group by FieldDefinitionId (#212): multi-value text fields have multiple rows per field (Order 0,1,2...), single-value fields have one row (Order 0).
        // Preallocate capacity by values.Count as an upper bound (deduplicated count <= this), avoiding dictionary growth for documents with many fields.
        var dict = new Dictionary<string, JsonElement>(values.Count, StringComparer.Ordinal);
        foreach (var group in values.GroupBy(v => v.FieldDefinitionId))
        {
            // FK RESTRICT ensures referenced field definitions cannot be hard-deleted; soft-deleted definitions are resolved by the traversing join.
            // In extreme missing cases, skip instead of emitting a half-baked key. Export JSON type is determined by FieldDefinition.DataType
            // (#208: not persisted on field value rows).
            if (!fieldDefs.TryGetValue(group.Key, out var def))
            {
                continue;
            }

            if (def.AllowMultiple)
            {
                // Multi-value field (#212): render as a JSON array ordered by Order ascending (export wire-shape: string[]).
                // This is symmetric with write paths (UpdateExtractedFieldsAsync / extraction both accept arrays), keeping operator read-edit-save round trips consistent.
                var array = group
                    .OrderBy(v => v.Order)
                    .Select(v => v.ToJsonElement(def.DataType))
                    .ToArray();
                dict[def.Name] = JsonSerializer.SerializeToElement(array);
            }
            else
            {
                // Single-value field: render the row with the smallest Order as a scalar. MinBy finds it in one pass without full sorting, preserving the existing wire-shape.
                var primary = group.MinBy(v => v.Order)!;
                dict[def.Name] = primary.ToJsonElement(def.DataType);
            }
        }

        return dict.Count > 0 ? dict : null;
    }

    /// <summary>Resolves one document's DocumentTypeId -> TypeCode, traversing soft-delete, for DocumentTypeCode carried by export ETOs.</summary>
    protected virtual async Task<string?> ResolveTypeCodeAsync(Guid? documentTypeId)
    {
        if (!documentTypeId.HasValue)
        {
            return null;
        }

        using (DataFilter.Disable<ISoftDelete>())
        {
            var type = await _documentTypeRepository.FindAsync(documentTypeId.Value);
            return type?.TypeCode;
        }
    }
}

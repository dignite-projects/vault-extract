using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Paperbase.Documents;

public class DocumentAppService : PaperbaseAppService, IDocumentAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;

    public DocumentAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        IBlobContainer<PaperbaseDocumentContainer> blobContainer,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _blobContainer = blobContainer;
        _pipelineRunManager = pipelineRunManager;
        _pipelineJobScheduler = pipelineJobScheduler;
        _distributedEventBus = distributedEventBus;
    }

    public virtual async Task<DocumentDto> GetAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    public virtual async Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        // 回收站视图：需要 Restore 权限，且整个查询管道必须在 DataFilter.Disable<ISoftDelete> 作用域内
        if (input.IsDeleted == true)
        {
            await CheckPolicyAsync(PaperbasePermissions.Documents.Restore);
            using (DataFilter.Disable<ISoftDelete>())
            {
                return await ExecuteListQueryAsync(input, onlyDeleted: true);
            }
        }

        return await ExecuteListQueryAsync(input, onlyDeleted: false);
    }

    protected virtual async Task<PagedResultDto<DocumentListItemDto>> ExecuteListQueryAsync(
        GetDocumentListInput input,
        bool onlyDeleted)
    {
        var query = await _documentRepository.GetQueryableAsync();
        query = ApplyFilter(query, input);
        if (onlyDeleted)
        {
            query = query.Where(d => d.IsDeleted);
        }

        var totalCount = await AsyncExecuter.CountAsync(query);

        query = ApplySorting(query, input.Sorting);
        query = query.Skip(input.SkipCount).Take(input.MaxResultCount);

        var documents = await AsyncExecuter.ToListAsync(query);

        return new PagedResultDto<DocumentListItemDto>(
            totalCount,
            ObjectMapper.Map<List<Document>, List<DocumentListItemDto>>(documents));
    }

    [Authorize(PaperbasePermissions.Documents.Upload)]
    public virtual async Task<DocumentDto> UploadAsync(UploadDocumentInput input)
    {
        // 前置检查：当前层至少要有一个 DocumentType（CLAUDE.md "两层文档类型体系" 单层精确匹配）。
        // Host 启动期 seed 入口已删除（HostDocumentTypeDataSeedContributor / DocumentTypeOptions），
        // DocumentType 现在只能通过 IDocumentTypeAppService 运行时创建——所以新部署 / 新租户必须先建类型才能上传。
        // 不做这个 fail-fast 检查的话，上传成功 → 分类候选集为空 → 文档永远卡 PendingReview。
        var hasType = (await _documentTypeRepository.GetByTenantAsync()).Any();
        if (!hasType)
        {
            throw new BusinessException(PaperbaseErrorCodes.NoDocumentTypesConfigured);
        }

        var fileName = input.File.FileName ?? "document";
        var contentType = input.File.ContentType ?? "application/octet-stream";
        var extension = Path.GetExtension(fileName);

        await using var source = input.File.GetStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var fileSize = bytes.LongLength;

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var existing = await _documentRepository.FindByContentHashAsync(contentHash);
        if (existing != null)
        {
            var errorCode = existing.IsDeleted
                ? PaperbaseErrorCodes.DocumentInRecycleBin
                : PaperbaseErrorCodes.DocumentDuplicate;

            throw new BusinessException(errorCode)
                .WithData("FileName", fileName)
                .WithData("ExistingDocumentId", existing.Id);
        }

        var blobName = GuidGenerator.Create().ToString("N") + extension;
        using (var saveStream = new MemoryStream(bytes, writable: false))
        {
            await _blobContainer.SaveAsync(blobName, saveStream);
        }

        var sourceType = SourceType.Physical; // placeholder；提取完成后由 BackgroundJob 回写实际值
        var fileOrigin = new FileOrigin(
            CurrentUser.UserName ?? string.Empty,
            contentType,
            contentHash,
            fileSize,
            originalFileName: fileName);

        var document = new Document(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            blobName,
            sourceType,
            fileOrigin);

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

        await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.TextExtraction);

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    public virtual async Task<IRemoteStreamContent> GetBlobAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        var stream = await _blobContainer.GetAsync(document.OriginalFileBlobName);

        return new RemoteStreamContent(
            stream,
            document.FileOrigin.OriginalFileName,
            document.FileOrigin.ContentType,
            disposeStream: true);
    }

    [Authorize(PaperbasePermissions.Documents.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id);

        await _documentRepository.DeleteAsync(id);

        // 通知下游消费方：Document 进入回收站，应将派生数据置为可恢复的归档状态
        await _distributedEventBus.PublishAsync(
            new DocumentDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode
            });
    }

    [Authorize(PaperbasePermissions.Documents.PermanentDelete)]
    public virtual async Task PermanentDeleteAsync(Guid id)
    {
        Document document;
        using (DataFilter.Disable<ISoftDelete>())
        {
            document = await _documentRepository.GetAsync(id, includeDetails: true);
        }

        await _documentRepository.HardDeleteAsync(id);

        try
        {
            await _blobContainer.DeleteAsync(document.OriginalFileBlobName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Failed to delete blob {BlobName} for document {DocumentId}.",
                document.OriginalFileBlobName, id);
        }

        // 通知下游消费方：Document 已不可恢复，应物理删除派生数据
        await _distributedEventBus.PublishAsync(
            new DocumentPermanentlyDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode
            });
    }

    [Authorize(PaperbasePermissions.Documents.Restore)]
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
                    EventTime = Clock.Now,
                    DocumentTypeCode = document.DocumentTypeCode
                });
        }
    }

    [Authorize(PaperbasePermissions.Documents.Export)]
    public virtual async Task<IRemoteStreamContent> GetExportAsync(GetDocumentListInput input)
    {
        var query = await _documentRepository.GetQueryableAsync();
        query = ApplyFilter(query, input);
        query = ApplySorting(query, input.Sorting);

        var documents = await AsyncExecuter.ToListAsync(query);
        var csv = BuildDocumentCsv(documents);
        var bytes = Encoding.UTF8.GetBytes(csv);

        return new RemoteStreamContent(new MemoryStream(bytes), "documents.csv", "text/csv");
    }

    /// <summary>
    /// 重试单条 pipeline。当前仅 <see cref="PipelineRunStatus.Failed"/> 可重试；
    /// Pending/Running 抛 <c>PipelineRetryInProgress</c>，Succeeded/Skipped 抛 <c>PipelineNotRetryable</c>。
    /// 重试先创建 Pending Run，再把带 PipelineRunId 的 BackgroundJob 入队。
    /// 链式重放语义（隐式）：重试 <c>text-extraction</c> → 成功后链触发 <c>classification</c>。
    /// </summary>
    [Authorize(PaperbasePermissions.Documents.Pipelines.Retry)]
    public virtual async Task RetryPipelineAsync(Guid id, RetryPipelineInput input)
    {
        if (!PaperbasePipelines.RetryablePipelines.Contains(input.PipelineCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.UnknownPipelineCode)
                .WithData("PipelineCode", input.PipelineCode);
        }

        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 显式租户断言 + 软删除门：不依赖 ambient DataFilter。
        if (document.TenantId != CurrentTenant.Id)
        {
            Logger.LogWarning(
                "RetryPipelineAsync tenant mismatch: doc={DocumentId} docTenant={DocTenantId} currentTenant={CurrentTenantId}",
                document.Id, document.TenantId, CurrentTenant.Id);
            throw new EntityNotFoundException(typeof(Document), id);
        }

        if (document.IsDeleted)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentInRecycleBin)
                .WithData("FileName", document.OriginalFileBlobName);
        }

        var latestRun = document.GetLatestRun(input.PipelineCode);
        if (latestRun == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.PipelineNeverRan)
                .WithData("PipelineCode", input.PipelineCode);
        }

        switch (latestRun.Status)
        {
            case PipelineRunStatus.Pending:
            case PipelineRunStatus.Running:
                throw new BusinessException(PaperbaseErrorCodes.PipelineRetryInProgress)
                    .WithData("PipelineCode", input.PipelineCode);
            case PipelineRunStatus.Succeeded:
            case PipelineRunStatus.Skipped:
                throw new BusinessException(PaperbaseErrorCodes.PipelineNotRetryable)
                    .WithData("PipelineCode", input.PipelineCode)
                    .WithData("Status", latestRun.Status.ToString());
        }

        Logger.LogInformation(
            "RetryPipelineAsync user={UserId} tenant={TenantId} doc={DocumentId} pipeline={PipelineCode} previousAttempt={Attempt}",
            CurrentUser.Id, CurrentTenant.Id, document.Id, input.PipelineCode, latestRun.AttemptNumber);

        await _pipelineJobScheduler.QueueAsync(document, input.PipelineCode);
    }

    /// <summary>
    /// 操作员手改字段抽取结果（个别纠错）。整体替换 ExtractedFields；key 必须是该文档所属层、
    /// 该 DocumentType 下已定义的字段名；完成后复用 FieldsExtractedEto 重发让下游同步。
    /// </summary>
    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 显式租户断言 — fail closed，不依赖 ambient DataFilter。
        if (document.TenantId != CurrentTenant.Id)
        {
            Logger.LogWarning(
                "UpdateExtractedFieldsAsync tenant mismatch: doc={DocumentId} docTenant={DocTenantId} currentTenant={CurrentTenantId}",
                document.Id, document.TenantId, CurrentTenant.Id);
            throw new EntityNotFoundException(typeof(Document), id);
        }

        // 字段定义挂在 DocumentType 下——未分类无从校验字段名。
        if (string.IsNullOrWhiteSpace(document.DocumentTypeCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentNotClassified);
        }

        // 校验每个 key 是该文档所属层、该 DocumentType 下已定义的字段名。
        // GetForExtractionAsync 按 ambient CurrentTenant.Id 查单层（已断言 == document.TenantId）。
        var definitions = await _fieldDefinitionRepository.GetForExtractionAsync(document.DocumentTypeCode);
        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var fields = input.Fields ?? new Dictionary<string, JsonElement>();

        foreach (var (key, value) in fields)
        {
            if (!definitionsByName.TryGetValue(key, out var definition))
            {
                throw new BusinessException(PaperbaseErrorCodes.UnknownExtractedField)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", document.DocumentTypeCode);
            }

            if (!IsValidExtractedFieldValue(value, definition.DataType))
            {
                throw new BusinessException(PaperbaseErrorCodes.InvalidExtractedFieldValue)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", document.DocumentTypeCode)
                    .WithData("DataType", definition.DataType.ToString())
                    .WithData("JsonValueKind", value.ValueKind.ToString());
            }
        }

        // 整体替换（与 FieldExtractionEventHandler 一致：空则清空）。值保留原始 JsonElement，
        // 仅校验 JSON 值类型符合 FieldDefinition.DataType，不做跨类型强制转换。
        document.SetExtractedFields(fields.Count > 0 ? fields : null);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        // 复用 FieldsExtractedEto 重发——手改与 LLM 抽取对下游是同一种"字段已更新"信号，
        // 下游按 (DocumentId, EventType, EventTime) 幂等、回拉最新字段值（出口契约：薄载荷）。
        await _distributedEventBus.PublishAsync(
            new FieldsExtractedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode,
                FieldCount = fields.Count
            });

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeCode);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeCode);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ApproveReviewAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        if (document.ReviewStatus != DocumentReviewStatus.PendingReview)
        {
            // 幂等：不在 PendingReview 状态下返回当前快照，不抛
            return ObjectMapper.Map<Document, DocumentDto>(document);
        }

        // 兑现 CLAUDE.md "OCR 置信度门槛" 承诺："操作员手动确认通过 → 触发 DocumentReadyEto"。
        // 两类待审核场景：
        //   (a) OCR confidence 不达标 → classification 尚未跑：schedule classification pipeline，
        //       让它正常推进，完成后由 DeriveLifecycle 跃到 Ready 自动发 DocumentReadyEto。
        //   (b) classification 已跑且有 DocumentTypeCode：直接 RecomputeLifecycle 让它进 Ready。
        //       分类已跑但 DocumentTypeCode 仍为空时，当前后台 pipeline 已经停在
        //       "没有已确认类型" 的审核结论上；不要抛客户端错误，也不要把它改成 Reviewed。
        //       保持 PendingReview + ClassificationReason，让操作员创建 DocumentType 后
        //       Reclassify，或重新上传更合适的源文件。
        var hasClassificationRun = document.GetLatestRun(PaperbasePipelines.Classification) != null;
        if (hasClassificationRun && string.IsNullOrWhiteSpace(document.DocumentTypeCode))
        {
            return ObjectMapper.Map<Document, DocumentDto>(document);
        }

        document.ApproveReview();

        if (!hasClassificationRun)
        {
            // QueueAsync 内已 _documentRepository.UpdateAsync(document, autoSave: true)，
            // 同一 document 实例的 ApproveReview() 状态变更随 scheduler 内的 save 一起落库；
            // 此分支无需再写一次。
            await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.Classification);
        }
        else
        {
            // RecomputeLifecycleAsync 仅修改 document 状态（in-memory），不写 DB——
            // 必须在这里显式 UpdateAsync 才能把 ApproveReview + RecomputeLifecycle 的变更落库。
            await _pipelineRunManager.RecomputeLifecycleAsync(document);
            await _documentRepository.UpdateAsync(document, autoSave: true);
        }

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        document.RejectReview(input.Reason);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    /// <summary>
    /// Confirm 与 Reclassify 共享实现：写入 TypeCode + Reviewed 状态，
    /// 发布 DocumentClassifiedEto 让下游消费方重跑字段抽取。
    /// </summary>
    protected virtual async Task<DocumentDto> ApplyManualClassificationAsync(Guid id, string documentTypeCode)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // typeCode 校验责任在 AppService（不再走 manager 内部 EnsureRegisteredTypeCodeAsync）：
        // 按 Document.TenantId 精确单层匹配（CLAUDE.md "两层 mutually exclusive"）；
        // 不存在则 fail-fast，避免写入"业务模块订阅者认不出的 typeCode"。
        var typeDef = await _documentTypeRepository.FindByTypeCodeAsync(documentTypeCode);
        if (typeDef == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidDocumentTypeCode)
                .WithData(nameof(documentTypeCode), documentTypeCode);
        }

        var run = await _pipelineRunManager.QueueAsync(document, PaperbasePipelines.Classification);
        await _pipelineRunManager.BeginAsync(document, run);

        await _pipelineRunManager.CompleteManualClassificationAsync(document, run, typeDef);
        await _distributedEventBus.PublishAsync(
            new DocumentClassifiedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = documentTypeCode,
                ClassificationConfidence = 1.0
            });

        await _documentRepository.UpdateAsync(document, autoSave: true);

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    protected virtual IQueryable<Document> ApplyFilter(IQueryable<Document> query, GetDocumentListInput input)
    {
        if (input.LifecycleStatus.HasValue)
            query = query.Where(x => x.LifecycleStatus == input.LifecycleStatus.Value);

        if (!input.DocumentTypeCode.IsNullOrWhiteSpace())
            query = query.Where(x => x.DocumentTypeCode == input.DocumentTypeCode);

        if (input.ReviewStatus.HasValue)
        {
            query = query.Where(d => d.ReviewStatus == input.ReviewStatus.Value);

            // PendingReview 队列默认只展示仍需处理的文档。RejectReview 会保留
            // ReviewStatus=PendingReview 作为审计信号，但 lifecycle 已是 Failed；
            // 若调用方确实要查失败审核记录，可显式传 LifecycleStatus=Failed。
            if (input.ReviewStatus.Value == DocumentReviewStatus.PendingReview &&
                !input.LifecycleStatus.HasValue)
            {
                query = query.Where(d => d.LifecycleStatus != DocumentLifecycleStatus.Failed);
            }
        }

        // Keyword 子串匹配（Title / 原始文件名 / Markdown 全文）。当前用 LIKE '%kw%'，
        // 文档量大时是顺序扫描；规模化后可在 Markdown 上建 SQL Server 全文索引替换。
        if (!input.Keyword.IsNullOrWhiteSpace())
        {
            var keyword = input.Keyword!.Trim();
            query = query.Where(d =>
                (d.Title != null && d.Title.Contains(keyword)) ||
                (d.FileOrigin.OriginalFileName != null && d.FileOrigin.OriginalFileName.Contains(keyword)) ||
                (d.Markdown != null && d.Markdown.Contains(keyword)));
        }

        return query;
    }

    protected virtual IQueryable<Document> ApplySorting(IQueryable<Document> query, string? sorting)
    {
        return sorting switch
        {
            "creationTime" => query.OrderBy(x => x.CreationTime),
            _ => query.OrderByDescending(x => x.CreationTime)
        };
    }

    private static string BuildDocumentCsv(List<Document> documents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,DocumentTypeCode,LifecycleStatus,OriginalFileName,ContentType,CreationTime");

        foreach (var d in documents)
        {
            sb.AppendLine(string.Join(",",
                d.Id,
                EscapeCsv(d.DocumentTypeCode),
                d.LifecycleStatus.ToString(),
                EscapeCsv(d.FileOrigin.OriginalFileName),
                EscapeCsv(d.FileOrigin.ContentType),
                d.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (value.IsNullOrEmpty()) return string.Empty;
        if (value!.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static bool IsValidExtractedFieldValue(JsonElement value, FieldDataType dataType)
    {
        return dataType switch
        {
            FieldDataType.String => value.ValueKind == JsonValueKind.String,
            FieldDataType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            FieldDataType.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            FieldDataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldDataType.Date => IsValidDateString(value),
            FieldDataType.DateTime => IsValidDateTimeString(value),
            _ => false
        };
    }

    private static bool IsValidDateString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParseExact(
                   value.GetString(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static bool IsValidDateTimeString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }
}

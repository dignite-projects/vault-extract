using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;

    public DocumentAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ICabinetRepository cabinetRepository,
        IBlobContainer<PaperbaseDocumentContainer> blobContainer,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _cabinetRepository = cabinetRepository;
        _blobContainer = blobContainer;
        _pipelineRunManager = pipelineRunManager;
        _pipelineJobScheduler = pipelineJobScheduler;
        _distributedEventBus = distributedEventBus;
    }

    public virtual async Task<DocumentDto> GetAsync(Guid id)
    {
        // 方法体内 programmatic 权限断言——同时是 MCP 出口（DocumentResources 委托此方法，#222）的权限防线：
        // MCP / 反射 / tool-dispatch 路径不经 HTTP [Authorize]，故此断言不得改写为类/方法级 [Authorize] 属性。
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        return await MapToDtoAsync(document);
    }

    public virtual async Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        // 解析外部类型码 → 内部 DocumentTypeId（#207）。提供了类型码但该层无此类型：带字段过滤 → loud fail
        // （字段无从解析）；仅元数据 → 空页（无该类型文档）。
        Guid? documentTypeId = null;
        if (!input.DocumentTypeCode.IsNullOrWhiteSpace())
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync(input.DocumentTypeCode!);
            if (type == null)
            {
                if (input.FieldFilters is { Count: > 0 })
                {
                    throw new BusinessException(PaperbaseErrorCodes.ExtractedField.Unknown)
                        .WithData("FieldName", input.FieldFilters[0].Name ?? string.Empty)
                        .WithData("DocumentTypeCode", input.DocumentTypeCode!);
                }
                return new PagedResultDto<DocumentListItemDto>(0, new List<DocumentListItemDto>());
            }
            documentTypeId = type.Id;
        }

        // ExtractedFields 字段值过滤器：把每个 FieldFilter 解析成带 FieldDefinitionId + 声明类型的 DocumentFieldQuery
        // （FieldDefinition 跨聚合查询，属调用层职责）。任一字段未在该类型下定义 → loud fail
        // （UnknownExtractedField，可纠正信号），不静默空。无 FieldFilters → null（仅元数据检索）。
        var fieldQueries = await ResolveFieldQueriesAsync(input, documentTypeId);

        // 回收站视图：需要 Restore 权限，且整个查询管道必须在 DataFilter.Disable<ISoftDelete> 作用域内
        if (input.IsDeleted == true)
        {
            await CheckPolicyAsync(PaperbasePermissions.Documents.Restore);
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

        // DTO 校验已保证有 FieldFilters 时 DocumentTypeCode 非空；上面已解析到 documentTypeId（非空，否则已 throw/return）。
        var fieldQueries = new List<DocumentFieldQuery>(input.FieldFilters.Count);
        foreach (var filter in input.FieldFilters)
        {
            var definition = await _fieldDefinitionRepository.FindByNameAsync(documentTypeId!.Value, filter.Name!);
            if (definition == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", filter.Name!)
                    .WithData("DocumentTypeCode", input.DocumentTypeCode!);
            }

            // 内部按 FieldDefinitionId 匹配 child 行（#207）；FieldName 仅用于仓储错误诊断。
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
        // 用 WithDetailsAsync(选择器) 只 eager-load ExtractedFieldValues child 行（不含 PipelineRuns）——
        // 持久化无关（ABP 仓储 API，避免 App 层直接依赖 EF Core .Include）。一次性 JOIN 取回 → 组装
        // ExtractedFields 字典，杜绝逐文档 N+1 / lazy loading（Issue #206 复核护栏）。
        var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);

        // ExtractedFields 字段值过滤：仓储用 Documents-anchored LINQ（child EXISTS + 类型化列比较）取（锚定
        // DocumentTypeId 的）匹配 Id 集合，再与本查询求交——保持 ApplyFilter 为元数据过滤单一来源。
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

        // DocumentTypeCode + ExtractedFields 字典 key 是 Id → code/name 投影：分页后一次性批量 join 填充（无 N+1）。
        await FillListReferencesAsync(documents, dtos);

        return new PagedResultDto<DocumentListItemDto>(totalCount, dtos);
    }

    [Authorize(PaperbasePermissions.Documents.Upload)]
    public virtual async Task<DocumentDto> UploadAsync(UploadDocumentInput input)
    {
        // 前置检查：当前层至少要有一个 DocumentType（CLAUDE.md "两层文档类型体系" 单层精确匹配）。
        // Host 启动期 seed 入口已删除（HostDocumentTypeDataSeedContributor / DocumentTypeOptions），
        // DocumentType 现在只能通过 IDocumentTypeAppService 运行时创建——所以新部署 / 新租户必须先建类型才能上传。
        // 不做这个 fail-fast 检查的话，上传成功 → 分类候选集为空 → 文档永远卡 PendingReview。
        var hasType = await _documentTypeRepository.GetCountAsync() > 0;
        if (!hasType)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentType.NoneConfigured);
        }

        // 文件柜归属校验（#194）：若指定 cabinetId，先断言 Cabinets 权限（fail-closed，与前端 canViewCabinets
        // gate 对称）——[Authorize(Documents.Upload)] 不覆盖 cabinet 归属，无此断言则无 Cabinets 权限者可绕过 UI
        // 把文档归到隐藏柜。再校验柜存在（租户隔离由 ambient IMultiTenant 过滤器施加，跨租户 FindAsync 返回 null）。
        // 柜正交于 pipeline——此处仅做上传时人工归属校验，后续 pipeline 不碰。
        if (input.CabinetId.HasValue)
        {
            await CheckPolicyAsync(PaperbasePermissions.Cabinets.Default);

            var cabinet = await _cabinetRepository.FindAsync(input.CabinetId.Value);
            if (cabinet == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.Cabinet.InvalidId)
                    .WithData("CabinetId", input.CabinetId.Value);
            }
        }

        var fileName = input.File.FileName ?? "document";
        var contentType = input.File.ContentType ?? "application/octet-stream";
        var extension = Path.GetExtension(fileName);

        // fail-closed 文件校验（#221）：content-type + 扩展名双重白名单。任意类型都会落 blob 并触发
        // 文本提取 / 分类 job，浪费算力且行为不确定——故不在通道允许集内立即 loud fail，不落 blob、不入队。
        // content-type 客户端可伪造，扩展名又决定 blob 后缀 + DefaultTextExtractor dispatch，故二者都校验。
        if (string.IsNullOrEmpty(extension) ||
            !DocumentConsts.AllowedUploadExtensions.Contains(extension) ||
            !DocumentConsts.AllowedUploadContentTypes.Contains(contentType))
        {
            throw new BusinessException(PaperbaseErrorCodes.Document.UnsupportedFileType)
                .WithData("FileName", fileName)
                .WithData("ContentType", contentType);
        }

        // 客户端声明的长度先做廉价拒绝（不可信，攻击者可少报或不报）——真正的边界是下方流式拷贝按
        // 实际字节数施加的硬上限，超限即刻中止，不把超大 body 全量缓冲进内存。
        if (input.File.ContentLength is > 0 and var declared && declared > DocumentConsts.MaxUploadFileBytes)
        {
            throw new BusinessException(PaperbaseErrorCodes.Document.FileTooLarge)
                .WithData("FileName", fileName)
                .WithData("MaxBytes", DocumentConsts.MaxUploadFileBytes);
        }

        // 在独立 using 作用域内缓冲：拿到 bytes 后立即释放 buffer，使 SaveAsync 期间只驻留 bytes（1×），
        // 消除此前 buffer + bytes 同时驻留的 ~2× 内存放大（#221 复审补充 1）。哈希需全量字节，无法完全流式。
        byte[] bytes;
        await using (var source = input.File.GetStream())
        using (var buffer = new MemoryStream())
        {
            await CopyWithLimitAsync(source, buffer, DocumentConsts.MaxUploadFileBytes, fileName);
            bytes = buffer.ToArray();
        }
        var fileSize = bytes.LongLength;

        var contentHash = ContentHasher.Sha256Hex(bytes);

        // 内容哈希去重是 check-then-act（ContentHash 仅非唯一索引）。两个并发的同文件上传可能双双通过检查 →
        // 产生重复 Document。本期有意接受该 race（#221 复审补充 2）：概率低、危害低（最多一份重复，可后续删），
        // 而给 ContentHash 加唯一索引会把 race 失败方变成一次 500——通道层不为低概率重复付这个代价。
        var existing = await _documentRepository.FindByContentHashAsync(contentHash);
        if (existing != null)
        {
            var errorCode = existing.IsDeleted
                ? PaperbaseErrorCodes.Document.InRecycleBin
                : PaperbaseErrorCodes.Document.Duplicate;

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

        await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.TextExtraction);

        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// 把 <paramref name="source"/> 拷贝进 <paramref name="destination"/>，按实际读取字节数施加硬上限（#221）。
    /// 一旦累计超过 <paramref name="maxBytes"/> 立即抛 <c>Document.FileTooLarge</c>——不依赖客户端声明的
    /// ContentLength（可伪造），也不把超大 body 全量缓冲进内存。
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
                throw new BusinessException(PaperbaseErrorCodes.Document.FileTooLarge)
                    .WithData("FileName", fileName)
                    .WithData("MaxBytes", maxBytes);
            }

            await destination.WriteAsync(rented.AsMemory(0, read));
        }
    }

    public virtual async Task<IRemoteStreamContent> GetBlobAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        // 只取 blob 流——仅需标量 + owned FileOrigin（随实体加载），不需要任何子集合。
        var document = await _documentRepository.GetAsync(id, includeDetails: false);
        var stream = await _blobContainer.GetAsync(document.FileOrigin.BlobName);

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
                EventTime = Clock.Now
            });
    }

    [Authorize(PaperbasePermissions.Documents.PermanentDelete)]
    public virtual async Task PermanentDeleteAsync(Guid id)
    {
        Document document;
        using (DataFilter.Disable<ISoftDelete>())
        {
            // 永久删除只需标量 + owned FileOrigin（blob 名），子集合一概不用。
            document = await _documentRepository.GetAsync(id, includeDetails: false);
        }

        await _documentRepository.HardDeleteAsync(id);

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

        // #210：永久删除时一并删归档的原生 payload blob（按 manifest 的稳定 key，不做 prefix 清理）。
        // 与原始文件 blob 同样 best-effort——删失败只记日志，不阻断永久删除主流程。
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

        // 通知下游消费方：Document 已不可恢复，应物理删除派生数据
        await _distributedEventBus.PublishAsync(
            new DocumentPermanentlyDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now
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
                    EventTime = Clock.Now
                });
        }
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
            throw new BusinessException(PaperbaseErrorCodes.Pipeline.UnknownCode)
                .WithData("PipelineCode", input.PipelineCode);
        }

        // 只需文档主行（IsDeleted / FileOrigin）+ 最近一次该 pipeline 的 run（判可重试）；不碰字段值。
        // 租户隔离由 ambient IMultiTenant 过滤器施加，GetAsync 对跨租户 / 不存在 id 抛 EntityNotFound。
        // #216 follow-up：retry 状态机判定下沉到 DocumentPipelineRunManager.EnsureRetryableAsync，
        // AppService 不再直接依赖 IDocumentPipelineRunRepository。
        var document = await _documentRepository.GetAsync(id, includeDetails: false);

        if (document.IsDeleted)
        {
            throw new BusinessException(PaperbaseErrorCodes.Document.InRecycleBin)
                .WithData("FileName", document.FileOrigin.BlobName);
        }

        var latestRun = await _pipelineRunManager.EnsureRetryableAsync(id, input.PipelineCode);

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
        // 租户隔离由 ambient IMultiTenant 过滤器施加——GetAsync 对跨租户 id 已抛 EntityNotFound。
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 字段定义挂在 DocumentType 下——未分类无从校验字段名。
        if (!document.DocumentTypeId.HasValue)
        {
            throw new BusinessException(PaperbaseErrorCodes.Document.NotClassified);
        }

        // ETO 仍携带 DocumentTypeCode 字符串（出口契约不变）——由内部 DocumentTypeId 解析（#207）。
        var documentTypeCode = await ResolveTypeCodeAsync(document.DocumentTypeId);

        // 校验每个 key 是该文档所属层、该 DocumentType 下已定义的字段名。
        // GetListAsync 按 ambient CurrentTenant.Id 查单层（已断言 == document.TenantId），按内部 DocumentTypeId 匹配。
        var definitions = await _fieldDefinitionRepository.GetListAsync(document.DocumentTypeId.Value);
        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var fields = input.Fields ?? new Dictionary<string, JsonElement>();

        // 校验每个值符合声明类型后，展开成 typed DocumentFieldValue（FieldDefinitionId + DataType 来自 FieldDefinition）。
        // 校验通过即可直接交给聚合根，不再经 JSON 字典中转——值类型与列对齐的转换集中在 DocumentExtractedField 内。
        // #212：多值文本字段的 JSON 数组由 DocumentFieldValueFactory 拆成多行（Order 0,1,2…），单值字段 1 行（Order 0）。
        var fieldValues = new List<DocumentFieldValue>(fields.Count);
        foreach (var (key, value) in fields)
        {
            if (!definitionsByName.TryGetValue(key, out var definition))
            {
                throw new BusinessException(PaperbaseErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty);
            }

            if (!ExtractedFieldValueValidator.IsValid(value, definition.DataType, definition.AllowMultiple))
            {
                throw new BusinessException(PaperbaseErrorCodes.ExtractedField.InvalidValue)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("DataType", definition.DataType.ToString())
                    .WithData("AllowMultiple", definition.AllowMultiple.ToString())
                    .WithData("JsonValueKind", value.ValueKind.ToString());
            }

            fieldValues.AddRange(DocumentFieldValueFactory.Expand(
                definition.Id, definition.DataType, definition.AllowMultiple, value));
        }

        // 整组替换（与 FieldExtractionEventHandler 一致：空则清空全部字段行）。
        document.SetFields(fieldValues);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        // FieldsExtractedEto.FieldCount 是逻辑字段数（产生 ≥1 个值的不同字段个数），非展开后的行数——
        // 与 FieldExtractionEventHandler 同一算法，保证两条写入路径对同一终态发出一致的薄信号
        // （多值字段空数组 [] 展开 0 行不计入，避免与 LLM 路径分叉）。下游按 (DocumentId, EventType, EventTime) 幂等、回拉最新字段值。
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

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeId);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeId);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        document.RejectReview(input.Reason);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// 改派文档所属文件柜（#257）。与 <see cref="UploadAsync"/> 的柜归属校验对称：
    /// 指派到某柜时断言 <see cref="PaperbasePermissions.Cabinets.Default"/> + 校验柜在当前层存在
    /// （租户隔离由 ambient IMultiTenant 过滤器施加，跨租户 FindAsync 返回 null）；移出（CabinetId == null）
    /// 仅需方法级 <see cref="PaperbasePermissions.Documents.Default"/>。柜正交于 pipeline——不触发任何后续 Run、不发出口事件。
    /// </summary>
    [Authorize(PaperbasePermissions.Documents.Default)]
    public virtual async Task<DocumentDto> UpdateCabinetAsync(Guid id, UpdateDocumentCabinetInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        if (input.CabinetId.HasValue)
        {
            await CheckPolicyAsync(PaperbasePermissions.Cabinets.Default);

            var cabinet = await _cabinetRepository.FindAsync(input.CabinetId.Value);
            if (cabinet == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.Cabinet.InvalidId)
                    .WithData("CabinetId", input.CabinetId.Value);
            }
        }

        document.SetCabinet(input.CabinetId);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        return await MapToDtoAsync(document);
    }

    /// <summary>
    /// Confirm 与 Reclassify 共享实现：按不可变 DocumentTypeId 解析类型后写入 Reviewed 状态，
    /// 发布 DocumentClassifiedEto（投射回可重命名 TypeCode 出口契约）让下游消费方重跑字段抽取。
    /// </summary>
    protected virtual async Task<DocumentDto> ApplyManualClassificationAsync(Guid id, Guid documentTypeId)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 类型校验责任在 AppService（不再走 manager 内部 EnsureRegisteredTypeCodeAsync）：
        // 按不可变 Id（#207）解析，租户隔离交给 ABP IMultiTenant 全局过滤器精确单层匹配；
        // 不存在则 fail-fast，避免写入"业务模块订阅者认不出的类型"。
        var typeDef = await _documentTypeRepository.FindAsync(documentTypeId);
        if (typeDef == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), documentTypeId);
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

        // 类型过滤用已解析的内部 DocumentTypeId（#207）。
        if (documentTypeId.HasValue)
            query = query.Where(x => x.DocumentTypeId == documentTypeId.Value);

        if (input.CabinetId.HasValue)
            query = query.Where(x => x.CabinetId == input.CabinetId.Value);

        // 按人工审核状态过滤。被拒绝的文档现在落 ReviewStatus=Rejected（#237）——既天然不出现在
        // PendingReview 队列，也可被调用方显式按 Rejected 查询；不再需要旧的"PendingReview 额外排除
        // LifecycleStatus=Failed"特例（那是 reject 文档曾停在 PendingReview 时的补丁）。
        if (input.ReviewStatus.HasValue)
            query = query.Where(d => d.ReviewStatus == input.ReviewStatus.Value);

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

    // ===== #207：Id → 外部 code/name 投影。内部存 DocumentTypeId / FieldDefinitionId，出口 DTO 仍输出 code/name；
    // 穿透 soft-delete 让历史文档引用的已归档类型 / 字段也能解析（不引入 snapshot 字段，rename 透明反映当前值）。=====

    /// <summary>映射单个 Document → DocumentDto 并填充 DocumentTypeCode + ExtractedFields（Id → code/name）。</summary>
    protected virtual async Task<DocumentDto> MapToDtoAsync(Document document)
    {
        var dto = ObjectMapper.Map<Document, DocumentDto>(document);
        var (typeCodes, fieldNames) = await ResolveReferenceMapsAsync(new[] { document });
        dto.DocumentTypeCode = ResolveTypeCode(document.DocumentTypeId, typeCodes);
        dto.ExtractedFields = AssembleExtractedFields(document.ExtractedFieldValues, fieldNames);
        return dto;
    }

    /// <summary>批量填充列表 DTO 的 DocumentTypeCode + ExtractedFields（分页后一次性解析两张映射表，无 N+1）。</summary>
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
        }
    }

    /// <summary>
    /// 一次性解析这批文档涉及的全部 DocumentTypeId → TypeCode 与 FieldDefinitionId → (Name, DataType, AllowMultiple) 映射。
    /// 穿透 soft-delete（已归档类型 / 字段仍可解析）；IMultiTenant 仍按 ambient 租户隔离（这批文档同属一层）。
    /// DataType 随 Name 一并取出（#208：字段类型由 FieldDefinition 决定、不在字段值行持久化），供 <see cref="DocumentExtractedField.ToJsonElement"/> 重建出口 JSON；
    /// AllowMultiple（#212）决定该字段在出口渲染为 JSON 数组（多值）还是标量（单值）。
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

        // 按 FieldDefinitionId 分组（#212）：多值文本字段一字段多行（Order 0,1,2…），单值一字段一行（Order 0）。
        // 容量按 values.Count 上界预留（去重后 ≤ 该值），省去多字段文档的字典扩容。
        var dict = new Dictionary<string, JsonElement>(values.Count, StringComparer.Ordinal);
        foreach (var group in values.GroupBy(v => v.FieldDefinitionId))
        {
            // FK RESTRICT 保证被引用字段定义不会被硬删；软删的由穿透 join 解析。极端缺失则跳过（不吐半成品 key）。
            // 出口 JSON 类型由 FieldDefinition.DataType 决定（#208：不在字段值行持久化）。
            if (!fieldDefs.TryGetValue(group.Key, out var def))
            {
                continue;
            }

            if (def.AllowMultiple)
            {
                // 多值字段（#212）：按 Order 升序渲染为 JSON 数组（出口 wire-shape：string[]）——与写入路径
                // （UpdateExtractedFieldsAsync / 抽取均收数组）对称，让 operator 读—改—存往返一致。
                var array = group
                    .OrderBy(v => v.Order)
                    .Select(v => v.ToJsonElement(def.DataType))
                    .ToArray();
                dict[def.Name] = JsonSerializer.SerializeToElement(array);
            }
            else
            {
                // 单值字段：取 Order 最小行渲染标量（MinBy 单趟取最小，不全排序），wire-shape 与既有完全一致。
                var primary = group.MinBy(v => v.Order)!;
                dict[def.Name] = primary.ToJsonElement(def.DataType);
            }
        }

        return dict.Count > 0 ? dict : null;
    }

    /// <summary>解析单个文档的 DocumentTypeId → TypeCode（穿透 soft-delete），用于出口 ETO 携带的 DocumentTypeCode。</summary>
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

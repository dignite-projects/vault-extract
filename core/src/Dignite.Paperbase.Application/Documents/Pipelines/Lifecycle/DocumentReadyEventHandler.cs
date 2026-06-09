using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.Paperbase.Documents.Pipelines.Lifecycle;

/// <summary>
/// 监听 <see cref="DocumentLifecycleStatusChangedEvent"/>，在文档跃迁到
/// <see cref="DocumentLifecycleStatus.Ready"/> 时发布 <see cref="DocumentReadyEto"/>——
/// CLAUDE.md "出口事件契约" 中下游消费方默认订阅的可信信号。
/// <para>
/// Ready 闸门由分类阶段执行：自动分类置信度不足 / 无合适类型的文档被置 blocking 原因 UnresolvedClassification（进待人工审核队列），
/// <c>DocumentTypeCode</c> 为空时 <c>DeriveLifecycle</c> 不会跃迁到 Ready。因此本 handler
/// 不需要额外校验，<c>NewStatus == Ready</c> 即隐含通过分类 / 人工审核闸门。
/// </para>
/// </summary>
public class DocumentReadyEventHandler
    : ILocalEventHandler<DocumentLifecycleStatusChangedEvent>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly ILogger<DocumentReadyEventHandler> _logger;

    public DocumentReadyEventHandler(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ILogger<DocumentReadyEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleEventAsync(DocumentLifecycleStatusChangedEvent eventData)
    {
        if (eventData.NewStatus != DocumentLifecycleStatus.Ready)
        {
            return;
        }

        var document = await _documentRepository.FindAsync(eventData.DocumentId, includeDetails: false);
        if (document == null)
        {
            _logger.LogWarning(
                "DocumentLifecycleStatusChangedEvent for missing document {DocumentId} — DocumentReadyEto not published.",
                eventData.DocumentId);
            return;
        }

        // ETO 仍携带 DocumentTypeCode 字符串（出口契约不变）——由内部 DocumentTypeId 解析（#207）。
        // Ready 文档必有已确认类型（DeriveLifecycle 闸门），且 DeleteAsync 阻止删除在用类型，故类型必活跃。
        string? documentTypeCode = null;
        if (document.DocumentTypeId.HasValue)
        {
            var type = await _documentTypeRepository.FindAsync(document.DocumentTypeId.Value);
            documentTypeCode = type?.TypeCode;
        }

        await _distributedEventBus.PublishAsync(
            new DocumentReadyEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now,
                DocumentTypeCode = documentTypeCode
            });

        _logger.LogInformation(
            "Document {DocumentId} reached Ready lifecycle; DocumentReadyEto enqueued (type={DocTypeCode}).",
            document.Id, documentTypeCode);
    }
}

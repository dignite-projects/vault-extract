using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L2 自动触发：订阅 <see cref="DocumentClassifiedEto"/>，
/// 在文档分类完成后排队 L2 背景任务。
///
/// <para>
/// <strong>触发时序</strong>：核心层与业务模块都订阅 <see cref="DocumentClassifiedEto"/>。
/// ABP 分布式事件总线本地实现按注册顺序串行调用所有订阅者；本订阅者只做"排队后台任务"
/// 这种轻量动作（不直接执行 L2），所以不会阻塞业务模块的字段抽取。
/// </para>
///
/// <para>
/// <strong>排队延迟（codex review 修复 [high] "L2 race"）</strong>：业务模块（如
/// <c>ContractDocumentHandler</c>）订阅同一事件并做 LLM 字段抽取（5–30s 典型）。
/// 如果 L2 后台任务在抽取完成前被工作线程拾取，<c>IDocumentIdentifierProvider</c>
/// 反查会得到空集，L2 会以 0 关系结束并标记 Succeeded（永不重试）。通过
/// <see cref="PaperbaseAIBehaviorOptions.RelationDiscoveryDelaySeconds"/>（默认 30 秒）
/// 给后台任务一个延迟启动时间，让所有同步 ETO 处理器先完成各自的 autoSave 抽取。
/// </para>
///
/// <para>
/// <strong>租户上下文（codex review 修复 [high] "Tenant context dropped"）</strong>：
/// 分布式事件处理器不能依赖 ambient <c>CurrentTenant</c>（消息总线可能跨进程派发，
/// 丢失上下文）。用 <c>using (_currentTenant.Change(eventData.TenantId))</c> 显式恢复，
/// 与 <c>ContractDocumentHandler</c> 同源。这样
/// <see cref="DocumentPipelineJobScheduler"/> 在创建 JobArgs 时读到的
/// <c>_currentTenant.Id</c> 是 eventData 携带的真实值，不是 ambient 残留。
/// </para>
///
/// <para>
/// <strong>orphan 文档</strong>：分类失败的文档不会发布 <see cref="DocumentClassifiedEto"/>，
/// 自然跳过 L2。这与 #115 设计 "v1 orphan 走 L3 兜底" 一致。
/// </para>
///
/// <para>
/// <strong>幂等性</strong>：<see cref="DocumentPipelineJobScheduler.QueueAsync"/> 每次创建新的
/// <c>DocumentPipelineRun</c>。重复运行 L2 也安全：<see cref="RelationDiscoveryService"/>
/// 对已存在关系做完全跳过，不会创建重复 AiSuggested 行。
/// </para>
/// </summary>
public class RelationDiscoveryEventHandler
    : IDistributedEventHandler<DocumentClassifiedEto>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly ICurrentTenant _currentTenant;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;
    private readonly ILogger<RelationDiscoveryEventHandler> _logger;

    public RelationDiscoveryEventHandler(
        IDocumentRepository documentRepository,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        ICurrentTenant currentTenant,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions,
        ILogger<RelationDiscoveryEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _pipelineJobScheduler = pipelineJobScheduler;
        _currentTenant = currentTenant;
        _aiOptions = aiOptions.Value;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        if (eventData.DocumentId == Guid.Empty)
        {
            return;
        }

        // Explicit tenant restoration — see XML doc above for rationale.
        using (_currentTenant.Change(eventData.TenantId))
        {
            // Document might have been hard-deleted between classification publish and handler dispatch.
            // FindAsync (not GetAsync) → silently drop instead of throwing into the event bus.
            var document = await _documentRepository.FindAsync(eventData.DocumentId, includeDetails: true);
            if (document == null)
            {
                _logger.LogInformation(
                    "L2 RelationDiscovery handler: document {DocumentId} no longer exists; skipping enqueue.",
                    eventData.DocumentId);
                return;
            }

            var delay = _aiOptions.RelationDiscoveryDelaySeconds > 0
                ? TimeSpan.FromSeconds(_aiOptions.RelationDiscoveryDelaySeconds)
                : (TimeSpan?)null;

            await _pipelineJobScheduler.QueueAsync(
                document,
                PaperbasePipelines.RelationDiscovery,
                delay);
        }
    }
}

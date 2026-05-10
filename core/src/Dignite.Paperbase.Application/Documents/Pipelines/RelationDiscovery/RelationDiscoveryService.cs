using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Services;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L2: 跨业务模块的关系发现服务。基于 <see cref="IDocumentIdentifierProvider"/>
/// 的 fan-out 查询，从源文档持有的标识符出发反查对端文档，自动创建
/// <see cref="RelationSource.AiSuggested"/> 类型的 <see cref="DocumentRelation"/>。
///
/// <para>
/// 算法（三阶段）：
/// <list type="number">
/// <item>收集源文档的所有标识符：遍历 <see cref="IDocumentIdentifierProvider"/>，
///       调用 <see cref="IDocumentIdentifierProvider.GetIdentifiersAsync"/>，得到该文档的 (type, value) 列表。</item>
/// <item>反查对端文档：对每个 (type, value)，遍历支持该 type 的 provider，
///       调用 <see cref="IDocumentIdentifierProvider.FindDocumentsAsync"/>。</item>
/// <item>去重 + 已有关系过滤后，为每个对端文档创建 AiSuggested DocumentRelation。</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>v1 范围</strong>：纯发现服务，不含自动触发（背景任务 + 事件订阅留给后续 PR）。
/// 调用方在文档分类完成 + 业务模块字段抽取完成后显式调用 <see cref="DiscoverAsync"/>。
/// 这避免与业务模块 <see cref="DocumentClassifiedEto"/> 处理器的时序竞态，并先把发现逻辑用 mock provider 测准。
/// </para>
///
/// <para>
/// <strong>不接 LLM</strong>：本服务是结构化匹配（标识符精确比较），无 prompt 表面，
/// 不需要 doc-chat 反例 C 那种"显式权限断言 + 静态描述 + Take(N)"工具体三件套。
/// </para>
/// </summary>
public class RelationDiscoveryService : DomainService
{
    /// <summary>结构化标识符匹配的固定置信度。L3 LLM 评判会输出动态 confidence；L2 是确定性匹配，固定高分。</summary>
    public const double StructuralMatchConfidence = 0.95;

    private readonly IEnumerable<IDocumentIdentifierProvider> _providers;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly ICurrentTenant _currentTenant;

    public RelationDiscoveryService(
        IEnumerable<IDocumentIdentifierProvider> providers,
        IDocumentRelationRepository relationRepository,
        ICurrentTenant currentTenant)
    {
        _providers = providers;
        _relationRepository = relationRepository;
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// 对指定源文档运行关系发现。返回新创建的 AiSuggested DocumentRelation 列表（用于 telemetry / 集成测试断言）。
    /// 已存在的关系（无论 Manual / AiSuggested / ModuleAuto）会被跳过——避免噪音建议覆盖用户已确认的关系。
    /// </summary>
    public virtual async Task<IReadOnlyList<DocumentRelation>> DiscoverAsync(
        Guid sourceDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (sourceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Source document id required.", nameof(sourceDocumentId));
        }

        // Phase 1: collect all identifiers held by source document across all providers.
        var sourceIdentifiers = await CollectSourceIdentifiersAsync(sourceDocumentId, cancellationToken);
        if (sourceIdentifiers.Count == 0)
        {
            // No identifiers extracted yet — either business modules haven't finished extraction,
            // or this document isn't owned by any module. v1 short-circuits; future re-queue
            // logic can wake L2 once providers have data.
            Logger.LogInformation(
                "L2 RelationDiscovery: no identifiers found for {DocumentId}; skipping (no business module owns it, or extraction pending)",
                sourceDocumentId);
            return Array.Empty<DocumentRelation>();
        }

        // Phase 2: for each identifier, fan out across providers that support its type to find peers.
        var peers = await FindPeerDocumentsAsync(sourceDocumentId, sourceIdentifiers, cancellationToken);
        if (peers.Count == 0)
        {
            return Array.Empty<DocumentRelation>();
        }

        // Phase 3: skip pairs that already have any relation (Manual/AiSuggested/ModuleAuto),
        // create AiSuggested for the rest. Skipping ALL existing relation kinds protects
        // user-confirmed relations from being re-suggested as if new.
        var alreadyLinked = await GetAlreadyLinkedPeersAsync(sourceDocumentId, cancellationToken);
        var created = new List<DocumentRelation>();

        foreach (var peer in peers)
        {
            if (alreadyLinked.Contains(peer.PeerDocumentId)) continue;

            var description = $"Identifier match: {peer.IdentifierType} = {peer.IdentifierValue}";
            var relation = new DocumentRelation(
                GuidGenerator.Create(),
                _currentTenant.Id,
                sourceDocumentId: sourceDocumentId,
                targetDocumentId: peer.PeerDocumentId,
                description: description,
                source: RelationSource.AiSuggested,
                confidence: StructuralMatchConfidence);

            await _relationRepository.InsertAsync(relation, autoSave: false, cancellationToken);
            created.Add(relation);

            // Add to alreadyLinked so multiple identifier matches against the same peer
            // create only one DocumentRelation (e.g. peer shares both ContractNumber and PartyName).
            alreadyLinked.Add(peer.PeerDocumentId);
        }

        Logger.LogInformation(
            "L2 RelationDiscovery: source={DocumentId} identifiers={IdentifierCount} peers={PeerCount} created={CreatedCount}",
            sourceDocumentId, sourceIdentifiers.Count, peers.Count, created.Count);

        return created;
    }

    protected virtual async Task<IReadOnlyList<DocumentIdentifierEntry>> CollectSourceIdentifiersAsync(
        Guid documentId,
        CancellationToken ct)
    {
        var entries = new List<DocumentIdentifierEntry>();
        foreach (var provider in _providers)
        {
            // Provider isolation: one buggy module must not tank the whole L2 discovery.
            // OperationCanceledException re-thrown so cancellation is honored.
            try
            {
                var fromProvider = await provider.GetIdentifiersAsync(documentId, ct);
                entries.AddRange(fromProvider);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex,
                    "L2 RelationDiscovery: provider {Provider} threw in GetIdentifiersAsync({DocumentId}); skipping its contribution",
                    provider.GetType().FullName, documentId);
            }
        }

        // De-duplicate (type, value) pairs — two providers could in principle report
        // the same identifier (rare but possible if a doc is dual-classified).
        return entries.Distinct().ToList();
    }

    protected virtual async Task<IReadOnlyList<PeerCandidate>> FindPeerDocumentsAsync(
        Guid sourceDocumentId,
        IReadOnlyList<DocumentIdentifierEntry> sourceIdentifiers,
        CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        var peers = new List<PeerCandidate>();

        foreach (var identifier in sourceIdentifiers)
        {
            var supportingProviders = _providers
                .Where(p => p.SupportedIdentifierTypes.Contains(identifier.Type));

            foreach (var provider in supportingProviders)
            {
                IReadOnlyList<Guid> found;
                try
                {
                    found = await provider.FindDocumentsAsync(identifier.Type, identifier.Value, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex,
                        "L2 RelationDiscovery: provider {Provider} threw in FindDocumentsAsync({Type}, {Value}); skipping",
                        provider.GetType().FullName, identifier.Type, identifier.Value);
                    continue;
                }

                foreach (var peerId in found)
                {
                    if (peerId == sourceDocumentId) continue;          // Self-match
                    if (peerId == Guid.Empty) continue;                // Defensive
                    if (!seen.Add(peerId)) continue;                   // Already a peer via another identifier

                    peers.Add(new PeerCandidate(peerId, identifier.Type, identifier.Value));
                }
            }
        }

        return peers;
    }

    protected virtual async Task<HashSet<Guid>> GetAlreadyLinkedPeersAsync(
        Guid sourceDocumentId,
        CancellationToken ct)
    {
        var existing = await _relationRepository.GetListByDocumentIdAsync(sourceDocumentId, ct);
        var linked = new HashSet<Guid>();
        foreach (var relation in existing)
        {
            var peer = relation.SourceDocumentId == sourceDocumentId
                ? relation.TargetDocumentId
                : relation.SourceDocumentId;
            linked.Add(peer);
        }
        return linked;
    }

    /// <summary>
    /// 中间数据：一个对端文档候选 + 触发本次匹配的 (type, value)。
    /// 当多个标识符同时命中同一对端时，第一个命中决定 Description（避免一对文档建多条关系）。
    /// </summary>
    protected sealed record PeerCandidate(Guid PeerDocumentId, string IdentifierType, string IdentifierValue);
}

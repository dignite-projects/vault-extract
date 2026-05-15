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
    private readonly IEnumerable<IDocumentIdentifierProvider> _providers;
    private readonly IEnumerable<IDocumentEntitySignatureProvider> _signatureProviders;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;

    // No ICurrentTenant injection (Issue #121): tenant flows through Document.TenantId
    // (loaded from the source document at the start of DiscoverAsync). Background jobs may
    // run without an ambient ICurrentTenant restored — explicit Document-derived tenant is
    // Hangfire-safe.
    public RelationDiscoveryService(
        IEnumerable<IDocumentIdentifierProvider> providers,
        IEnumerable<IDocumentEntitySignatureProvider> signatureProviders,
        IDocumentRelationRepository relationRepository,
        IDocumentRepository documentRepository,
        RelationDiscoveryTelemetryRecorder telemetry)
    {
        _providers = providers;
        _signatureProviders = signatureProviders;
        _relationRepository = relationRepository;
        _documentRepository = documentRepository;
        _telemetry = telemetry;
    }

    /// <summary>
    /// 对指定源文档运行关系发现。返回新创建的 AiSuggested DocumentRelation 列表（用于 telemetry / 集成测试断言）。
    /// 已存在的关系（无论 Manual 或 AiSuggested）会被跳过——避免噪音建议覆盖用户已确认的关系。
    /// </summary>
    public virtual async Task<IReadOnlyList<DocumentRelation>> DiscoverAsync(
        Guid sourceDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (sourceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Source document id required.", nameof(sourceDocumentId));
        }

        // Phase 0: load source to capture authoritative TenantId (Issue #121).
        // Document hard-deleted between event publish and L2 execution → drop silently.
        var source = await _documentRepository.FindAsync(sourceDocumentId, cancellationToken: cancellationToken);
        if (source == null)
        {
            Logger.LogInformation(
                "L2 RelationDiscovery: document {DocumentId} no longer exists; skipping",
                sourceDocumentId);
            return Array.Empty<DocumentRelation>();
        }
        var sourceTenantId = source.TenantId;

        // Phase 1: collect all identifiers held by source document across all providers.
        // Each DocumentIdentifierEntry carries both raw display form AND the normalized
        // comparison key — normalization is provider-owned (Issue #159 open-contract reform),
        // so L2 just trusts the entry as-is.
        var sourceIdentifiers = await CollectSourceIdentifiersAsync(sourceDocumentId, cancellationToken);

        // Phase 1b (硬伤二 L2 Phase 3): collect multi-field entity signatures. Modules emit
        // these for relationships that don't share a single identifier (e.g. main contract
        // and its supplement, where each has its own ContractNumber but they share parties +
        // year). Lower inherent confidence than single-identifier matches (0.80 vs 0.95).
        var sourceSignatures = await CollectSourceSignaturesAsync(sourceDocumentId, cancellationToken);

        if (sourceIdentifiers.Count == 0 && sourceSignatures.Count == 0)
        {
            // No identifiers AND no signatures — either business modules haven't finished
            // extraction, or this document isn't owned by any module. 硬伤三 visibility:
            // orphan-document signal lets operators spot extraction regressions.
            _telemetry.RecordOrphanDocument();
            Logger.LogInformation(
                "L2 RelationDiscovery: no identifiers or signatures found for {DocumentId}; skipping " +
                "(no business module owns it, or extraction pending)",
                sourceDocumentId);
            return Array.Empty<DocumentRelation>();
        }

        // Phase 2: fan out across both single-identifier providers and multi-field signature
        // providers. Each producer returns peer candidates carrying their own confidence
        // (0.95 deterministic for identifier, lower statistical confidence for signature)
        // and a human-readable description used directly on the DocumentRelation.
        // Identifier path runs FIRST so its higher-confidence description wins when a peer
        // is matched both ways (single-identifier match is essentially deterministic).
        var peers = await FindPeerDocumentsAsync(sourceDocumentId, sourceIdentifiers, cancellationToken);
        var signaturePeers = await FindPeerDocumentsBySignatureAsync(sourceDocumentId, sourceSignatures, cancellationToken);

        // Merge: signature path peers only added when not already discovered via identifier
        // path (first-match-wins on description + confidence; identifier path is the stronger
        // signal so it takes precedence).
        var seenInBoth = new HashSet<Guid>(peers.Select(p => p.PeerDocumentId));
        foreach (var sp in signaturePeers)
        {
            if (seenInBoth.Add(sp.PeerDocumentId)) peers.Add(sp);
        }

        if (peers.Count == 0)
        {
            return Array.Empty<DocumentRelation>();
        }

        // Phase 3: skip pairs that already have any relation (Manual or AiSuggested),
        // create AiSuggested for the rest. Skipping ALL existing relation kinds protects
        // user-confirmed relations from being re-suggested as if new. R2: also skip dismissed
        // tombstones (IsDeleted=true) so users who reject an AI suggestion don't see it
        // resurface on the next run.
        var alreadyLinked = await GetAlreadyLinkedPeersAsync(sourceDocumentId, sourceTenantId, cancellationToken);
        var created = new List<DocumentRelation>();

        foreach (var peer in peers)
        {
            if (alreadyLinked.Contains(peer.PeerDocumentId)) continue;

            // TenantId from source.TenantId (Hangfire-safe, Issue #121). Source and peers
            // must be in the same tenant: providers query through ABP's IMultiTenant filter
            // which scopes by ambient or by source — peers never cross tenant by design.
            var relation = new DocumentRelation(
                GuidGenerator.Create(),
                sourceTenantId,
                sourceDocumentId: sourceDocumentId,
                targetDocumentId: peer.PeerDocumentId,
                description: peer.Description,
                source: RelationSource.AiSuggested);

            await _relationRepository.InsertAsync(relation, autoSave: false, cancellationToken);
            created.Add(relation);

            // Add to alreadyLinked so multiple matches against the same peer create only one
            // DocumentRelation (e.g. peer shares both ContractNumber and the PartiesAndYear signature).
            alreadyLinked.Add(peer.PeerDocumentId);
        }

        Logger.LogInformation(
            "L2 RelationDiscovery: source={DocumentId} identifiers={IdentifierCount} signatures={SignatureCount} peers={PeerCount} created={CreatedCount}",
            sourceDocumentId, sourceIdentifiers.Count, sourceSignatures.Count, peers.Count, created.Count);

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
            IReadOnlyList<DocumentIdentifierEntry> fromProvider;
            try
            {
                fromProvider = await provider.GetIdentifiersAsync(documentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex,
                    "L2 RelationDiscovery: provider {Provider} threw in GetIdentifiersAsync({DocumentId}); skipping its contribution",
                    provider.GetType().FullName, documentId);
                // 硬伤三: record 0 contribution so dashboards see "this provider exists but
                // failed for this run" (vs "provider isn't installed", which produces no
                // sample at all).
                _telemetry.RecordIdentifiersByProvider(provider.GetType().Name, 0);
                continue;
            }

            entries.AddRange(fromProvider);
            // 硬伤三: per-provider contribution counter, separate from total — useful for
            // dashboards that want "which module is producing identifiers" breakdowns.
            _telemetry.RecordIdentifiersByProvider(provider.GetType().Name, fromProvider.Count);
        }

        // L2 trusts provider-side normalization (Issue #159 open-contract reform). Each entry
        // already carries both the raw display form AND the normalized comparison key — produced
        // by the provider using whatever rule fits its type's semantics. L2 dedupes on
        // (Type, NormalizedValue) and uses RawValue for human-readable descriptions; the
        // central layer no longer knows or decides normalization strategy. See
        // IDocumentIdentifierProvider docs for the cross-module governance contract.
        return entries
            .Where(e => !string.IsNullOrEmpty(e.NormalizedValue))
            .GroupBy(e => (e.Type, e.NormalizedValue))
            .Select(g => g.First())
            .ToList();
    }

    protected virtual async Task<List<PeerCandidate>> FindPeerDocumentsAsync(
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

            // 硬伤三: tally distinct peers this single (type, normalized-value) found across
            // all providers in this run. If it exceeds the high-ambiguity threshold, emit a
            // telemetry + WARN log so operators can find values like "001" or "项目" that
            // collide with too many documents and should be filtered out at the provider.
            var distinctPeersForIdentifier = new HashSet<Guid>();

            foreach (var provider in supportingProviders)
            {
                IReadOnlyList<Guid> found;
                try
                {
                    // L2 sends NormalizedValue to providers; the provider compares it against
                    // its own indexed normalized column (e.g. Contract.NormalizedContractNumber).
                    // Normalization is the provider's responsibility — L2 just routes by type.
                    found = await provider.FindDocumentsAsync(identifier.Type, identifier.NormalizedValue, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex,
                        "L2 RelationDiscovery: provider {Provider} threw in FindDocumentsAsync({Type}, {NormalizedValue}); skipping",
                        provider.GetType().FullName, identifier.Type, identifier.NormalizedValue);
                    continue;
                }

                foreach (var peerId in found)
                {
                    if (peerId == sourceDocumentId) continue;          // Self-match
                    if (peerId == Guid.Empty) continue;                // Defensive

                    distinctPeersForIdentifier.Add(peerId);

                    if (!seen.Add(peerId)) continue;                   // Already a peer via another identifier

                    // Description uses RawValue (user-recognizable form, e.g. "HT-2024-001").
                    peers.Add(new PeerCandidate(
                        PeerDocumentId: peerId,
                        Description: $"Identifier match: {identifier.Type} = {identifier.Value}"));
                }
            }

            if (distinctPeersForIdentifier.Count >= RelationDiscoveryTelemetryRecorder.HighAmbiguityPeerThreshold)
            {
                _telemetry.RecordHighAmbiguityIdentifier(
                    identifier.Type, identifier.NormalizedValue, distinctPeersForIdentifier.Count);
            }
        }

        return peers;
    }

    /// <summary>
    /// 硬伤二 (L2 Phase 3): collect multi-field entity signatures from all signature providers.
    /// Same per-provider exception isolation pattern as identifiers.
    /// </summary>
    protected virtual async Task<IReadOnlyList<DocumentEntitySignature>> CollectSourceSignaturesAsync(
        Guid documentId,
        CancellationToken ct)
    {
        var signatures = new List<DocumentEntitySignature>();
        foreach (var provider in _signatureProviders)
        {
            try
            {
                var fromProvider = await provider.GetSignaturesAsync(documentId, ct);
                signatures.AddRange(fromProvider);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex,
                    "L2 RelationDiscovery: signature provider {Provider} threw in GetSignaturesAsync({DocumentId}); skipping",
                    provider.GetType().FullName, documentId);
            }
        }
        return signatures;
    }

    /// <summary>
    /// 硬伤二 (L2 Phase 3): for each collected signature, fan out across providers that declare
    /// the same Kind and collect their FindDocumentsBySignatureAsync results. Same self/empty
    /// guards and high-ambiguity detection as the identifier path.
    /// </summary>
    protected virtual async Task<IReadOnlyList<PeerCandidate>> FindPeerDocumentsBySignatureAsync(
        Guid sourceDocumentId,
        IReadOnlyList<DocumentEntitySignature> sourceSignatures,
        CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        var peers = new List<PeerCandidate>();

        foreach (var signature in sourceSignatures)
        {
            var supportingProviders = _signatureProviders
                .Where(p => p.SupportedSignatureKinds.Contains(signature.Kind));

            var distinctPeersForSignature = new HashSet<Guid>();

            foreach (var provider in supportingProviders)
            {
                IReadOnlyList<Guid> found;
                try
                {
                    found = await provider.FindDocumentsBySignatureAsync(signature, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex,
                        "L2 RelationDiscovery: signature provider {Provider} threw in FindDocumentsBySignatureAsync({Kind}); skipping",
                        provider.GetType().FullName, signature.Kind);
                    continue;
                }

                foreach (var peerId in found)
                {
                    if (peerId == sourceDocumentId) continue;
                    if (peerId == Guid.Empty) continue;

                    distinctPeersForSignature.Add(peerId);

                    if (!seen.Add(peerId)) continue;

                    peers.Add(new PeerCandidate(
                        PeerDocumentId: peerId,
                        Description: BuildSignatureDescription(signature)));
                }
            }

            if (distinctPeersForSignature.Count >= RelationDiscoveryTelemetryRecorder.HighAmbiguityPeerThreshold)
            {
                _telemetry.RecordHighAmbiguityIdentifier(
                    signature.Kind, BuildSignatureDescription(signature), distinctPeersForSignature.Count);
            }
        }

        return peers;
    }

    /// <summary>
    /// Build a human-readable description for an entity signature match: e.g.
    /// <c>"Entity signature: Contracts.PartiesAndYear (PartyA=ACME, PartyB=BetaCorp, Year=2024)"</c>.
    /// Field order is dictionary order — providers should keep field names stable.
    /// </summary>
    protected virtual string BuildSignatureDescription(DocumentEntitySignature signature)
    {
        var fieldList = string.Join(", ", signature.Fields.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"Entity signature: {signature.Kind} ({fieldList})";
    }

    protected virtual async Task<HashSet<Guid>> GetAlreadyLinkedPeersAsync(
        Guid sourceDocumentId,
        Guid? sourceTenantId,
        CancellationToken ct)
    {
        // R2 dismissal tombstone: includeDismissed=true so dismissed (soft-deleted) rows
        // count as "already linked" and block re-suggestion. Tenant id passed explicitly
        // because IgnoreQueryFilters disables both soft-delete AND tenant filters
        // (EF Core all-or-nothing pre-EF10) — repo re-applies the tenant predicate.
        var peerIds = await _relationRepository.GetLinkedPeerDocumentIdsAsync(
            sourceDocumentId,
            sourceTenantId,
            includeDismissed: true,
            ct);
        return new HashSet<Guid>(peerIds);
    }

    /// <summary>
    /// 中间数据：一个对端文档候选 + 该匹配的描述。当多个匹配同时命中同一对端时，
    /// 第一个命中决定保留的 PeerCandidate（避免一对文档建多条关系）。
    /// </summary>
    protected sealed record PeerCandidate(Guid PeerDocumentId, string Description);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Services;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L3：基于"向量召回 + LLM 评判"的语义关系发现，作为 L2 (<see cref="RelationDiscoveryService"/>)
/// 找不到任何结构化匹配时的兜底路径。
///
/// <para>
/// <strong>触发时机</strong>：仅在 L2 返回 0 关系时由 <see cref="RelationDiscoveryBackgroundJob"/> 调用，
/// 且 <see cref="PaperbaseAIBehaviorOptions.EnableSemanticRelationDiscovery"/> 必须为 <c>true</c>
/// （默认关闭——LLM 成本按文档数线性增长）。
/// </para>
///
/// <para>
/// <strong>三阶段流水线</strong>：
/// <list type="number">
/// <item>向量召回：用源文档 Markdown 头部生成 embedding，在向量库取 top-K 高相似度文档（高于普通 RAG 阈值）。</item>
/// <item>预过滤：排除自身 / 已存在关系的文档。</item>
/// <item>LLM 评判：把每对文档的 Markdown 摘要交给 <see cref="RelationInferenceAgent"/>，
///       confidence 超阈值才创建 AiSuggested DocumentRelation；description 来自 LLM 输出。</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>orphan 文档支持</strong>：源文档不属于任何业务模块（DocumentTypeCode 已分类但未注册业务模块）
/// 也能走 L3——本路径不依赖 <c>IDocumentIdentifierProvider</c>，只看 Markdown 与向量库。
/// </para>
///
/// <para>
/// <strong>fail-closed</strong>：tenant 显式从 <c>Document.TenantId</c> 取（不依赖 ambient
/// <c>CurrentTenant</c>，与 Embedding pipeline 同一逻辑）；候选异常隔离（一个 LLM 调用失败不打断后续）。
/// </para>
/// </summary>
public class SemanticRelationDiscoveryService : DomainService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly RelationInferenceAgent _inferenceAgent;
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    // No ICurrentTenant injection: tenant flows through DocumentSnapshot.TenantId, sourced from
    // Document.TenantId (Hangfire-safe). Background jobs may run without an ambient ICurrentTenant.
    public SemanticRelationDiscoveryService(
        IDocumentRepository documentRepository,
        IDocumentRelationRepository relationRepository,
        IDocumentKnowledgeIndex knowledgeIndex,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RelationInferenceAgent inferenceAgent,
        RelationDiscoveryTelemetryRecorder telemetry,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions)
    {
        _documentRepository = documentRepository;
        _relationRepository = relationRepository;
        _knowledgeIndex = knowledgeIndex;
        _embeddingGenerator = embeddingGenerator;
        _inferenceAgent = inferenceAgent;
        _telemetry = telemetry;
        _aiOptions = aiOptions.Value;
    }

    /// <summary>
    /// 对源文档运行 L3 发现。返回新创建的 AiSuggested 关系列表。
    /// 如果 <see cref="PaperbaseAIBehaviorOptions.EnableSemanticRelationDiscovery"/>=false，
    /// 立即返回空列表（短路，不走任何 IO/LLM）。
    /// </summary>
    public virtual async Task<IReadOnlyList<DocumentRelation>> DiscoverAsync(
        Guid sourceDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (!_aiOptions.EnableSemanticRelationDiscovery)
        {
            return Array.Empty<DocumentRelation>();
        }

        if (sourceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Source document id required.", nameof(sourceDocumentId));
        }

        // Phase 1: load source. Reject docs without Markdown — no semantic signal to search on.
        var source = await _documentRepository.FindAsync(sourceDocumentId, cancellationToken: cancellationToken);
        if (source == null || string.IsNullOrWhiteSpace(source.Markdown))
        {
            return Array.Empty<DocumentRelation>();
        }

        // Phase 2: vector recall — search by source's Markdown head (where titles / unique identifiers
        // typically live). Use Document.TenantId explicitly (Hangfire-safe; no ambient context dependency).
        var candidates = await RecallCandidatesAsync(source, cancellationToken);
        if (candidates.Count == 0)
        {
            return Array.Empty<DocumentRelation>();
        }

        // Phase 3: filter peers already linked by ANY relation kind. Same rule as L2 — don't
        // re-suggest what user already saw / dismissed / confirmed.
        var alreadyLinked = await GetAlreadyLinkedAsync(sourceDocumentId, cancellationToken);
        var freshCandidates = candidates.Where(id => !alreadyLinked.Contains(id)).ToList();
        if (freshCandidates.Count == 0)
        {
            return Array.Empty<DocumentRelation>();
        }

        // Phase 4: per-candidate LLM evaluation. Provider-isolation: one bad LLM call must not
        // tank the rest of the candidates. TenantId carried via snapshot so the write path
        // (EvaluateAndCreateAsync) doesn't depend on ambient ICurrentTenant — Hangfire-safe,
        // matches the explicit-tenant strategy used by RecallCandidatesAsync.
        var sourceSnapshot = new DocumentSnapshot(source.TenantId, source.DocumentTypeCode, source.Markdown);
        var created = new List<DocumentRelation>();

        foreach (var candidateId in freshCandidates)
        {
            var relation = await EvaluateAndCreateAsync(
                sourceDocumentId, sourceSnapshot, candidateId, cancellationToken);
            if (relation != null)
            {
                created.Add(relation);
            }
        }

        Logger.LogInformation(
            "L3 SemanticRelationDiscovery: source={DocumentId} candidates={CandidateCount} fresh={FreshCount} llmConfirmed={CreatedCount}",
            sourceDocumentId, candidates.Count, freshCandidates.Count, created.Count);

        return created;
    }

    protected virtual async Task<IReadOnlyList<Guid>> RecallCandidatesAsync(
        Document source,
        CancellationToken ct)
    {
        // Use the markdown head — typically carries title + first paragraph + key identifiers.
        // Cheaper than embedding the entire markdown and usually more representative for short queries.
        var queryText = TruncateForEmbedding(source.Markdown!);

        Embedding<float> queryEmbedding;
        try
        {
            var batch = await _embeddingGenerator.GenerateAsync(new[] { queryText }, cancellationToken: ct);
            queryEmbedding = batch[0];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "L3 SemanticRelationDiscovery: embedding generation failed for {DocumentId}; skipping",
                source.Id);
            return Array.Empty<Guid>();
        }

        IReadOnlyList<VectorSearchResult> results;
        try
        {
            results = await _knowledgeIndex.SearchAsync(new VectorSearchRequest
            {
                TenantId = source.TenantId,
                QueryVector = queryEmbedding.Vector,
                TopK = _aiOptions.SemanticRelationDiscoveryTopK,
                MinScore = _aiOptions.SemanticRelationDiscoveryMinScore,
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "L3 SemanticRelationDiscovery: vector search failed for {DocumentId}; skipping",
                source.Id);
            return Array.Empty<Guid>();
        }

        // Aggregate chunk hits by DocumentId; exclude self.
        return results
            .Select(r => r.DocumentId)
            .Where(id => id != source.Id && id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    protected virtual async Task<DocumentRelation?> EvaluateAndCreateAsync(
        Guid sourceDocumentId,
        DocumentSnapshot sourceSnapshot,
        Guid candidateId,
        CancellationToken ct)
    {
        var candidate = await _documentRepository.FindAsync(candidateId, cancellationToken: ct);
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.Markdown))
        {
            return null;
        }

        var candidateSnapshot = new DocumentSnapshot(candidate.TenantId, candidate.DocumentTypeCode, candidate.Markdown);

        RelationInferenceResult inference;
        try
        {
            inference = await _inferenceAgent.EvaluateAsync(sourceSnapshot, candidateSnapshot, ct);
        }
        catch (Exception ex)
        {
            // Per-candidate LLM failure: log & skip. Other candidates still get evaluated.
            Logger.LogError(ex,
                "L3 SemanticRelationDiscovery: LLM evaluation failed for ({Source}, {Candidate}); skipping pair",
                sourceDocumentId, candidateId);
            _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Error);
            return null;
        }

        if (!inference.IsRelated || inference.Confidence < _aiOptions.SemanticRelationDiscoveryConfidenceThreshold)
        {
            _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Rejected);
            return null;
        }

        var description = string.IsNullOrWhiteSpace(inference.Description)
            ? "Semantic match (LLM-evaluated)"
            : inference.Description!.Trim();

        // TenantId from sourceSnapshot (Hangfire-safe — no ambient context dependency).
        // Source and candidate must be in the same tenant: ABP IMultiTenant filter on the
        // vector search guarantees that. We trust source.TenantId as the authoritative value.
        var relation = new DocumentRelation(
            GuidGenerator.Create(),
            sourceSnapshot.TenantId,
            sourceDocumentId: sourceDocumentId,
            targetDocumentId: candidateId,
            description: description,
            source: RelationSource.AiSuggested,
            confidence: inference.Confidence);

        // autoSave: true (vs L2's autoSave: false). Reason: L3 mixes external LLM calls with
        // repository writes inside the loop. Wrapping all of L3 in an outer UoW would hold a
        // DB connection during LLM calls — direct violation of .claude/rules/background-jobs.md.
        // Per-insert auto-save uses an implicit per-call UoW; LLM calls between candidates
        // happen with NO ambient UoW, satisfying the rule.
        await _relationRepository.InsertAsync(relation, autoSave: true, ct);
        _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Confirmed);
        return relation;
    }

    protected virtual async Task<HashSet<Guid>> GetAlreadyLinkedAsync(Guid sourceDocumentId, CancellationToken ct)
    {
        var existing = await _relationRepository.GetListByDocumentIdAsync(sourceDocumentId, ct);
        var linked = new HashSet<Guid>();
        foreach (var rel in existing)
        {
            var peer = rel.SourceDocumentId == sourceDocumentId ? rel.TargetDocumentId : rel.SourceDocumentId;
            linked.Add(peer);
        }
        return linked;
    }

    private string TruncateForEmbedding(string markdown)
    {
        // Embedding models have token limits (typically ~8K tokens). MaxTextLengthPerExtraction
        // is in chars, conservatively ~half the embedding model limit. We embed the head, not the
        // middle/tail, because document headers carry the highest-signal text (titles, identifiers).
        var max = _aiOptions.MaxTextLengthPerExtraction;
        return markdown.Length <= max ? markdown : markdown.Substring(0, max);
    }
}

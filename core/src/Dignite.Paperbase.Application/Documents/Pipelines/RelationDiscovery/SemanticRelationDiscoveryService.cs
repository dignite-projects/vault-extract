using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
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
    private readonly DocumentChunkCollectionProvider _collectionProvider;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly RelationInferenceAgent _inferenceAgent;
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    // No ICurrentTenant injection: tenant flows through DocumentSnapshot.TenantId, sourced from
    // Document.TenantId (Hangfire-safe). Background jobs may run without an ambient ICurrentTenant.
    public SemanticRelationDiscoveryService(
        IDocumentRepository documentRepository,
        IDocumentRelationRepository relationRepository,
        DocumentChunkCollectionProvider collectionProvider,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RelationInferenceAgent inferenceAgent,
        RelationDiscoveryTelemetryRecorder telemetry,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions)
    {
        _documentRepository = documentRepository;
        _relationRepository = relationRepository;
        _collectionProvider = collectionProvider;
        _embeddingGenerator = embeddingGenerator;
        _inferenceAgent = inferenceAgent;
        _telemetry = telemetry;
        _aiOptions = aiOptions.Value;
    }

    /// <summary>
    /// 对源文档运行 L3 发现。返回新创建的 AiSuggested 关系列表 + 漏斗统计（Y4 telemetry granularity）。
    /// 如果 <see cref="PaperbaseAIBehaviorOptions.EnableSemanticRelationDiscovery"/>=false，
    /// 立即返回空 outcome（短路，不走任何 IO/LLM）。
    /// </summary>
    public virtual async Task<SemanticDiscoveryOutcome> DiscoverAsync(
        Guid sourceDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (!_aiOptions.EnableSemanticRelationDiscovery)
        {
            return SemanticDiscoveryOutcome.Empty;
        }

        if (sourceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Source document id required.", nameof(sourceDocumentId));
        }

        // Phase 1: load source. Reject docs without Markdown — no semantic signal to search on.
        var source = await _documentRepository.FindAsync(sourceDocumentId, cancellationToken: cancellationToken);
        if (source == null || string.IsNullOrWhiteSpace(source.Markdown))
        {
            return SemanticDiscoveryOutcome.Empty;
        }

        // Phase 2: vector recall — search by source's Markdown head (where titles / unique identifiers
        // typically live). Use Document.TenantId explicitly (Hangfire-safe; no ambient context dependency).
        var candidates = await RecallCandidatesAsync(source, cancellationToken);
        if (candidates.Count == 0)
        {
            return new SemanticDiscoveryOutcome(Array.Empty<DocumentRelation>(), CandidatesRecalled: 0, CandidatesEvaluated: 0, CircuitBroken: false);
        }

        // Phase 3: filter peers already linked by ANY relation kind, INCLUDING dismissed
        // (soft-deleted) tombstones. Same rule as L2 — don't re-suggest what user already
        // saw / dismissed / confirmed. R2: dismissal-aware lookup avoids the "clear the bin,
        // it fills back up next run" UX failure.
        var alreadyLinked = await GetAlreadyLinkedAsync(sourceDocumentId, source.TenantId, cancellationToken);
        var freshCandidates = candidates.Where(id => !alreadyLinked.Contains(id)).ToList();
        if (freshCandidates.Count == 0)
        {
            return new SemanticDiscoveryOutcome(Array.Empty<DocumentRelation>(), CandidatesRecalled: candidates.Count, CandidatesEvaluated: 0, CircuitBroken: false);
        }

        // Phase 4: per-candidate LLM evaluation. Provider-isolation: one bad LLM call must not
        // tank the rest of the candidates. TenantId carried via snapshot so the write path
        // (EvaluateAndCreateAsync) doesn't depend on ambient ICurrentTenant — Hangfire-safe,
        // matches the explicit-tenant strategy used by RecallCandidatesAsync.
        var sourceSnapshot = new DocumentSnapshot(source.TenantId, source.DocumentTypeCode, source.Markdown);
        var created = new List<DocumentRelation>();

        // Consecutive-failure short-circuit (codex review fix [high] R4 "Hangfire worker pool starvation"):
        // continuous N candidates throwing (timeouts/HTTP errors/etc.) → bail out of remaining candidates.
        // Single transient hiccup tolerated; sustained failure treated as provider outage.
        var consecutiveFailures = 0;
        var cutoff = Math.Max(1, _aiOptions.SemanticRelationDiscoveryConsecutiveFailureCutoff);
        var circuitBroken = false;
        var evaluated = 0;

        foreach (var candidateId in freshCandidates)
        {
            evaluated++;
            var (relation, failed) = await EvaluateAndCreateAsync(
                sourceDocumentId, sourceSnapshot, candidateId, cancellationToken);
            if (relation != null)
            {
                created.Add(relation);
                consecutiveFailures = 0;
            }
            else if (failed)
            {
                if (++consecutiveFailures >= cutoff)
                {
                    Logger.LogError(
                        "L3 SemanticRelationDiscovery: {Cutoff} consecutive candidate failures for source {DocumentId}; " +
                        "treating LLM provider as unavailable and bailing out of remaining {Remaining} candidates.",
                        cutoff, sourceDocumentId, freshCandidates.Count - evaluated);
                    circuitBroken = true;
                    break;
                }
            }
            else
            {
                consecutiveFailures = 0;
            }
        }

        Logger.LogInformation(
            "L3 SemanticRelationDiscovery: source={DocumentId} recalled={Recalled} fresh={FreshCount} evaluated={Evaluated} llmConfirmed={CreatedCount} circuitBroken={CircuitBroken}",
            sourceDocumentId, candidates.Count, freshCandidates.Count, evaluated, created.Count, circuitBroken);

        return new SemanticDiscoveryOutcome(created, CandidatesRecalled: candidates.Count, CandidatesEvaluated: evaluated, CircuitBroken: circuitBroken);
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

        // MinScore lives on PaperbaseAIBehaviorOptions (not PaperbaseVectorStoreOptions) because
        // L3 uses a higher threshold than ordinary RAG to bias toward strong matches. Applied
        // client-side because MEVD's SearchAsync doesn't expose a server-side score threshold.
        var tenantKey = DocumentChunkPayloadEncoding.EncodeTenantId(source.TenantId);
        var sourceDocKey = DocumentChunkPayloadEncoding.EncodeDocumentId(source.Id);
        var minScore = _aiOptions.SemanticRelationDiscoveryMinScore;

        var collection = await _collectionProvider.GetAsync(ct);
        var hitDocumentIds = new HashSet<Guid>();
        try
        {
            await foreach (var hit in collection.SearchAsync(
                queryEmbedding.Vector,
                top: _aiOptions.SemanticRelationDiscoveryTopK,
                new VectorSearchOptions<DocumentChunkRecord>
                {
                    Filter = r => r.TenantId == tenantKey && r.DocumentId != sourceDocKey,
                    VectorProperty = r => r.Embedding,
                },
                cancellationToken: ct))
            {
                if (minScore > 0 && hit.Score is double score && score < minScore)
                {
                    continue;
                }

                if (Guid.TryParse(hit.Record.DocumentId, out var docId) && docId != Guid.Empty)
                {
                    hitDocumentIds.Add(docId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "L3 SemanticRelationDiscovery: vector search failed for {DocumentId}; skipping",
                source.Id);
            return Array.Empty<Guid>();
        }

        return hitDocumentIds.ToList();
    }

    /// <summary>
    /// Returns (relation, failed). `relation != null` → created and saved. `failed == true` →
    /// transient LLM/provider error (caller increments consecutive-failure counter for R4
    /// circuit breaker). Both null/false → skipped legitimately (no markdown / LLM said
    /// IsRelated=false / confidence below threshold).
    /// </summary>
    protected virtual async Task<(DocumentRelation? Relation, bool Failed)> EvaluateAndCreateAsync(
        Guid sourceDocumentId,
        DocumentSnapshot sourceSnapshot,
        Guid candidateId,
        CancellationToken ct)
    {
        var candidate = await _documentRepository.FindAsync(candidateId, cancellationToken: ct);
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.Markdown))
        {
            return (null, false);
        }

        var candidateSnapshot = new DocumentSnapshot(candidate.TenantId, candidate.DocumentTypeCode, candidate.Markdown);

        // Per-call timeout (codex review fix [high] R4): provider default timeouts are too generous
        // (OpenAI/Azure ~100s) → 5 candidates × 100s = 8min single doc blocks worker pool. Linked CTS
        // honors caller cancellation too. Throws TaskCanceledException on timeout, caught below.
        RelationInferenceResult inference;
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _aiOptions.SemanticRelationDiscoveryPerCallTimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            inference = await _inferenceAgent.EvaluateAsync(sourceSnapshot, candidateSnapshot, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation — propagate, do not treat as provider failure.
            throw;
        }
        catch (Exception ex)
        {
            // Per-candidate LLM failure (including timeout from linkedCts): log & skip. Other
            // candidates still get evaluated up to the consecutive-failure cutoff (R4).
            Logger.LogError(ex,
                "L3 SemanticRelationDiscovery: LLM evaluation failed for ({Source}, {Candidate}); skipping pair",
                sourceDocumentId, candidateId);
            _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Error);
            return (null, true);
        }

        // Confidence clamp (codex review fix [high] R3): LLM occasionally returns out-of-range
        // values despite the prompt's [0,1] constraint. Without clamping, `new DocumentRelation`
        // throws in ValidateConfidence → caught by outer `catch (Exception)` → silently logged
        // as Error → user never sees this AiSuggested relation that the LLM otherwise agreed with.
        // Clamp + warn keeps the signal visible while preserving the aggregate invariant.
        //
        // R3-followup [2nd-round review]: Math.Clamp does NOT normalize NaN — NaN compares false
        // against both bounds so Clamp returns NaN unchanged. Subsequent `NaN < threshold` is
        // also false, so NaN flows into the relation and persists. ASP.NET Core's default
        // JsonSerializer throws on NaN → poisons the relation list GET endpoint with HTTP 500.
        // Reject non-finite values up front; +/- Infinity get clamped to bounds correctly.
        if (double.IsNaN(inference.Confidence) || double.IsInfinity(inference.Confidence))
        {
            Logger.LogWarning(
                "L3 SemanticRelationDiscovery: LLM returned non-finite confidence {Raw} for ({Source}, {Candidate}); treating as Rejected",
                inference.Confidence, sourceDocumentId, candidateId);
            _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Rejected);
            return (null, false);
        }

        var clamped = Math.Clamp(inference.Confidence, 0d, 1d);
        if (Math.Abs(clamped - inference.Confidence) > double.Epsilon)
        {
            Logger.LogWarning(
                "L3 SemanticRelationDiscovery: LLM returned out-of-range confidence {Raw} for ({Source}, {Candidate}); clamped to {Clamped}",
                inference.Confidence, sourceDocumentId, candidateId, clamped);
        }

        if (!inference.IsRelated || clamped < _aiOptions.SemanticRelationDiscoveryConfidenceThreshold)
        {
            _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Rejected);
            return (null, false);
        }

        var description = string.IsNullOrWhiteSpace(inference.Description)
            ? "Semantic match (LLM-evaluated)"
            : inference.Description!.Trim();

        // TenantId from sourceSnapshot (Hangfire-safe — no ambient context dependency).
        // Source and candidate must be in the same tenant: the vector store filter on the
        // recall step guarantees that. We trust source.TenantId as the authoritative value.
        var relation = new DocumentRelation(
            GuidGenerator.Create(),
            sourceSnapshot.TenantId,
            sourceDocumentId: sourceDocumentId,
            targetDocumentId: candidateId,
            description: description,
            source: RelationSource.AiSuggested,
            confidence: clamped);

        // autoSave: true (vs L2's autoSave: false). Reason: L3 mixes external LLM calls with
        // repository writes inside the loop. Wrapping all of L3 in an outer UoW would hold a
        // DB connection during LLM calls — direct violation of .claude/rules/background-jobs.md.
        // Per-insert auto-save uses an implicit per-call UoW; LLM calls between candidates
        // happen with NO ambient UoW, satisfying the rule.
        await _relationRepository.InsertAsync(relation, autoSave: true, ct);
        _telemetry.RecordL3LlmCall(RelationDiscoveryL3CallResult.Confirmed);
        return (relation, false);
    }

    protected virtual async Task<HashSet<Guid>> GetAlreadyLinkedAsync(
        Guid sourceDocumentId,
        Guid? sourceTenantId,
        CancellationToken ct)
    {
        // R2 dismissal tombstone: includeDismissed=true so dismissed (soft-deleted) rows
        // count as "already linked" and block re-suggestion. Tenant id passed explicitly
        // (Hangfire-safe — no ambient ICurrentTenant dependency).
        var peerIds = await _relationRepository.GetLinkedPeerDocumentIdsAsync(
            sourceDocumentId,
            sourceTenantId,
            includeDismissed: true,
            ct);
        return new HashSet<Guid>(peerIds);
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

/// <summary>
/// Y4: L3 run outcome — relations created plus funnel-granularity counts so per-run telemetry
/// can distinguish "0 created because recall empty" / "0 because all already linked" /
/// "0 because LLM rejected all" / "stopped because circuit broke".
/// </summary>
public sealed record SemanticDiscoveryOutcome(
    IReadOnlyList<DocumentRelation> Relations,
    int CandidatesRecalled,
    int CandidatesEvaluated,
    bool CircuitBroken)
{
    public static readonly SemanticDiscoveryOutcome Empty =
        new(Array.Empty<DocumentRelation>(), CandidatesRecalled: 0, CandidatesEvaluated: 0, CircuitBroken: false);
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Abstractions.Chat;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Chat.Search;

/// <summary>
/// Bridges <see cref="IDocumentKnowledgeIndex"/> to MAF tool calling, exposing
/// vector search as an LLM-callable <see cref="AIFunction"/> via
/// <see cref="CreateSearchFunction"/>. The adapter owns the work the framework can't
/// do on its own:
/// <list type="bullet">
///   <item>Embed the query because the vector store search requires a vector.</item>
///   <item>Carry an explicit <see cref="VectorSearchRequest.TenantId"/> so the search
///         is safe under Hangfire / CLI scenarios where ABP ambient context is absent.</item>
///   <item>Format result chunks into a prompt block with provenance metadata
///         (<c>&lt;document id chunk page&gt;</c> tags), the only payload the LLM sees.</item>
/// </list>
///
/// This adapter is the shared document retrieval path used by document chat.
/// </summary>
public class DocumentTextSearchAdapter : ITransientDependency
{
    private readonly IDocumentKnowledgeIndex _vectorStore;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly DocumentRerankWorkflow _rerankWorkflow;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;
    private readonly PaperbaseKnowledgeIndexOptions _ragOptions;
    private readonly ILogger<DocumentTextSearchAdapter> _logger;

    public DocumentTextSearchAdapter(
        IDocumentKnowledgeIndex vectorStore,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        DocumentRerankWorkflow rerankWorkflow,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions,
        IOptions<PaperbaseKnowledgeIndexOptions> ragOptions,
        ILogger<DocumentTextSearchAdapter> logger)
    {
        _vectorStore = vectorStore;
        _embeddingGenerator = embeddingGenerator;
        _rerankWorkflow = rerankWorkflow;
        _aiOptions = aiOptions.Value;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Formats the context block returned to the agent. Each chunk is wrapped in
    /// <c>&lt;document id="…" chunk="…"&gt;</c> tags; the chunk text is passed through
    /// <see cref="PromptBoundary.WrapDocument"/> so that any <c>&lt;</c> characters are
    /// escaped to <c>&amp;lt;</c> before injection, preventing tag-injection attacks.
    /// Override in a subclass to customize the prompt structure.
    /// </summary>
    protected virtual string FormatSearchContext(IReadOnlyList<VectorSearchResult> vectorResults)
    {
        var sb = new StringBuilder();
        foreach (var vr in vectorResults)
        {
            var pageAttr = vr.PageNumber.HasValue ? $" page=\"{vr.PageNumber}\"" : "";
            sb.AppendLine($"<document id=\"{vr.DocumentId:D}\" chunk=\"{vr.ChunkIndex}\"{pageAttr}>");
            sb.AppendLine(PromptBoundary.WrapDocument(vr.Text ?? string.Empty));
            sb.AppendLine("</document>");
        }
        return sb.ToString();
    }

    protected virtual async Task<IReadOnlyList<VectorSearchResult>> SearchVectorAsync(
        Guid? tenantId,
        DocumentSearchScope? scope,
        string query,
        CancellationToken cancellationToken = default)
    {
        var finalTopK = scope?.TopK ?? _ragOptions.DefaultTopK;
        var rerank = _aiOptions.EnableLlmRerank && finalTopK > 0;
        var recallTopK = rerank
            ? finalTopK * Math.Max(1, _aiOptions.RecallExpandFactor)
            : finalTopK;

        var embeddings = await _embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken);

        // DocumentIds (multi) supersedes DocumentId (single) when provided.
        var hasMultiIds = scope?.DocumentIds?.Count > 0;
        var request = new VectorSearchRequest
        {
            TenantId = tenantId,
            QueryVector = embeddings[0].Vector,
            TopK = recallTopK,
            DocumentId = hasMultiIds ? null : scope?.DocumentId,
            DocumentIds = hasMultiIds ? scope!.DocumentIds : null,
            DocumentTypeCode = scope?.DocumentTypeCode,
            MinScore = scope?.MinScore ?? _ragOptions.MinScore,
            QueryText = query
        };

        var results = await _vectorStore.SearchAsync(request, cancellationToken);
        if (!rerank || results.Count <= finalTopK)
        {
            return results.Take(finalTopK).ToList();
        }

        var candidates = results
            .Select(r => new RerankCandidate(r.Text, r.Score ?? 0.0, r))
            .ToList();

        var reranked = await _rerankWorkflow.RerankAsync(
            query,
            candidates,
            finalTopK,
            cancellationToken);

        return reranked
            .Select(r => (VectorSearchResult)r.Candidate.Tag!)
            .ToList();
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> named <paramref name="functionName"/> that exposes
    /// vector search as an LLM-callable tool. Accepts an optional <c>documentIds</c> parameter
    /// so the LLM can restrict the search to documents returned by earlier tool calls
    /// (e.g. <c>search_contracts</c> → <c>search_paperbase_documents</c>).
    ///
    /// <para>
    /// The returned function logs its call arguments and latency at Information level and
    /// sets <paramref name="capture"/> so that citations remain available after the turn.
    /// </para>
    /// </summary>
    public virtual AIFunction CreateSearchFunction(
        Guid? tenantId,
        DocumentSearchScope? baseScope,
        DocumentSearchCapture capture,
        DocumentChatToolContext toolContext,
        IDocumentChatToolFactory toolFactory,
        string functionName,
        string functionDescription)
    {
        var binding = new SearchFunctionBinding(this, tenantId, baseScope, capture);
        return toolFactory.Create(
            toolContext,
            binding.InvokeAsync,
            name: functionName,
            description: functionDescription);
    }

    // ── nested helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the bound context for the <c>search_paperbase_documents</c> AIFunction.
    /// Factored into a class so parameter-level <see cref="DescriptionAttribute"/>s are
    /// accessible via reflection (lambda parameters cannot carry attributes in C#).
    /// </summary>
    private sealed class SearchFunctionBinding
    {
        private readonly DocumentTextSearchAdapter _adapter;
        private readonly Guid? _tenantId;
        private readonly DocumentSearchScope? _baseScope;
        private readonly DocumentSearchCapture _capture;

        public SearchFunctionBinding(
            DocumentTextSearchAdapter adapter,
            Guid? tenantId,
            DocumentSearchScope? baseScope,
            DocumentSearchCapture capture)
        {
            _adapter = adapter;
            _tenantId = tenantId;
            _baseScope = baseScope;
            _capture = capture;
        }

        public async Task<string> InvokeAsync(
            [Description("Search query text — describe what information you are looking for. Will be embedded for vector similarity search.")]
            string query,
            [Description("Optional document IDs to restrict the search. Pass IDs returned by other tools (e.g. search_contracts) to focus the RAG search on specific documents — do not invent IDs from raw user input.")]
            Guid[]? documentIds = null,
            [Description("Optional document type code to restrict the search to a single type (e.g. 'contract.general', 'receipt.general'). Useful for cross-document reconciliation when narrowing to receipts/invoices/etc.")]
            string? documentTypeCode = null,
            [Description("Number of top chunks to return. Default 5; raise to 10–15 for cross-document reconciliation when broader recall helps.")]
            int? topK = null,
            [Description("Minimum cosine similarity in [0,1] for hits to be returned. Default 0.45; raise for strict-match queries, lower for cross-language / proper-noun lookups.")]
            double? minScore = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            // Issue #100: the previous "single-document conversation" boundary that
            // ignored model-supplied documentIds is gone. ChatConversation no longer
            // pins a DocumentId / DocumentTypeCode at the scope level, so the model is
            // free (and encouraged — see ChatInstructionsBuilder.MultiStepReasoningGuidance)
            // to widen / focus the search per turn. The real safety boundary is _tenantId
            // (closure-captured from the conversation aggregate, threaded into
            // VectorSearchRequest.TenantId by SearchVectorAsync) — that the LLM cannot
            // override via tool arguments.
            var scope = new DocumentSearchScope
            {
                DocumentIds = documentIds is { Length: > 0 } ? documentIds : null,
                DocumentTypeCode = string.IsNullOrWhiteSpace(documentTypeCode)
                    ? _baseScope?.DocumentTypeCode
                    : documentTypeCode,
                TopK = topK ?? _baseScope?.TopK,
                MinScore = minScore ?? _baseScope?.MinScore
            };

            var vectorResults = await _adapter.SearchVectorAsync(_tenantId, scope, query, cancellationToken);
            _capture.Append(vectorResults);

            sw.Stop();
            // Argument hashing + audit are recorded by AuditedDocumentChatFunction; do not
            // log the raw `query` here — it usually contains the user's natural-language
            // input or LLM rephrasing thereof, which can include PII.
            _adapter._logger.LogInformation(
                "doc-chat search_paperbase_documents queryLength={Length} documentIds={Ids} type={TypeCode} topK={TopK} minScore={MinScore} results={Count} latency={Latency}ms",
                query?.Length ?? 0,
                documentIds == null ? "(none)" : string.Join(",", documentIds),
                scope.DocumentTypeCode ?? "(none)",
                scope.TopK,
                scope.MinScore,
                vectorResults.Count,
                sw.ElapsedMilliseconds);

            return _adapter.FormatSearchContext(vectorResults);
        }
    }
}

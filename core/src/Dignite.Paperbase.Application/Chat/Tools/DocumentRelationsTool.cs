using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Chat;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Linq;

namespace Dignite.Paperbase.Chat.Tools;

/// <summary>
/// Issue #101: contributes the <c>get_document_relations</c> AIFunction to the
/// chat agent. Lets the model walk the <see cref="DocumentRelation"/> graph from
/// an anchor document to the documents it has been linked to (manually by users
/// or by a future AI inference workflow). Closes the gap that pure vector search
/// cannot bridge — "what receipts are tied to this contract?" — without forcing
/// the model to guess document IDs from raw user input.
/// <para>
/// Lives in the core tool stack (alongside <c>search_paperbase_documents</c>),
/// not in any business module: <see cref="DocumentRelation"/> is a Core
/// aggregate, and the relationship is owner-agnostic (any pair of documents
/// regardless of business module).
/// </para>
/// <para>
/// fail-closed safety contract — see <c>.claude/rules/doc-chat-anti-patterns.md</c>
/// reverse example C: explicit <c>Documents.Default</c> permission check, explicit
/// tenant predicate (never rely on ambient ABP <c>DataFilter</c>), hard <see cref="MaxResultRows"/>
/// upper bound, static-constant tool name &amp; description.
/// </para>
/// </summary>
public class DocumentRelationsTool : ITransientDependency
{
    private const int MaxResultRows = 20;

    private readonly IDocumentRelationRepository _repository;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IAuthorizationService _authorizationService;

    public DocumentRelationsTool(
        IDocumentRelationRepository repository,
        IAsyncQueryableExecuter asyncExecuter,
        IAuthorizationService authorizationService)
    {
        _repository = repository;
        _asyncExecuter = asyncExecuter;
        _authorizationService = authorizationService;
    }

    public virtual AIFunction CreateAIFunction(
        DocumentChatToolContext ctx,
        IDocumentChatToolFactory toolFactory)
    {
        var binding = new Binding(_repository, _asyncExecuter, _authorizationService, ctx.TenantId);
        return toolFactory.Create(
            ctx,
            binding.GetRelationsAsync,
            name: "get_document_relations",
            description:
                "Look up the documents related to a given document via the DocumentRelation graph " +
                "(both manual user-confirmed links and AI-suggested links). " +
                "Use this BEFORE search_paperbase_documents when the question implies cross-document evidence " +
                "(e.g. 'has this contract been paid?' → call get_document_relations on the contract, then " +
                "drill into the returned documentIds with search_paperbase_documents). " +
                "Returns up to 20 entries, ordered with manual links first, then AI-suggested links by confidence descending.",
            // Issue #116 progress description: documentId is opaque to the user; the
            // anchor doc is implicit from context. Generic label is fine here.
            progressDescriber: _ => "正在查找该文档的关联文档…");
    }

    /// <summary>
    /// Holds the bound context for the <c>get_document_relations</c> AIFunction.
    /// Factored so parameter-level <see cref="DescriptionAttribute"/>s are accessible
    /// via reflection (lambda parameters cannot carry attributes in C#).
    /// </summary>
    private sealed class Binding
    {
        private readonly IDocumentRelationRepository _repository;
        private readonly IAsyncQueryableExecuter _asyncExecuter;
        private readonly IAuthorizationService _authorizationService;
        private readonly Guid? _tenantId;

        public Binding(
            IDocumentRelationRepository repository,
            IAsyncQueryableExecuter asyncExecuter,
            IAuthorizationService authorizationService,
            Guid? tenantId)
        {
            _repository = repository;
            _asyncExecuter = asyncExecuter;
            _authorizationService = authorizationService;
            _tenantId = tenantId;
        }

        public async Task<string> GetRelationsAsync(
            [Description("Anchor document ID to look up relations for. Returns documents linked to it (in either direction).")]
            Guid documentId,
            CancellationToken cancellationToken = default)
        {
            await _authorizationService.CheckAsync(PaperbasePermissions.Documents.Default);

            var queryable = await _repository.GetQueryableAsync();

            // Explicit tenant predicate — defense-in-depth against any code path that
            // disables ABP's ambient DataFilter (background jobs, non-HTTP contexts).
            queryable = _tenantId.HasValue
                ? queryable.Where(r => r.TenantId == _tenantId)
                : queryable.Where(r => r.TenantId == null);

            // Bidirectional lookup: relations are symmetric in semantics even though
            // SourceDocumentId / TargetDocumentId encode a directed edge — the user is
            // equally interested in "what does X link to" and "what links to X".
            queryable = queryable.Where(r =>
                r.SourceDocumentId == documentId || r.TargetDocumentId == documentId);

            // Manual relations come first (user-confirmed = highest signal); within the
            // AI-suggested bucket, descending by Confidence. Source enum: Manual=0,
            // AiSuggested=1, so OrderBy(Source) naturally puts Manual first. Take(N)
            // bounds context-window cost.
            var relations = await _asyncExecuter.ToListAsync(
                queryable
                    .OrderBy(r => r.Source)
                    .ThenByDescending(r => r.Confidence)
                    .Take(MaxResultRows),
                cancellationToken);

            var payload = new
            {
                anchorDocumentId = documentId,
                count = relations.Count,
                relations = relations.Select(r => new
                {
                    id = r.Id,
                    sourceDocumentId = r.SourceDocumentId,
                    targetDocumentId = r.TargetDocumentId,
                    // The "other side" relative to the anchor — convenience field so the
                    // model doesn't have to reason about edge direction itself.
                    relatedDocumentId = r.SourceDocumentId == documentId
                        ? r.TargetDocumentId
                        : r.SourceDocumentId,
                    description = r.Description,
                    source = r.Source.ToString(),   // "Manual" / "AiSuggested"
                    confidence = r.Confidence
                })
            };

            return JsonSerializer.Serialize(payload);
        }
    }
}

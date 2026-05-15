using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Permissions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Linq;
using Volo.Abp.MultiTenancy;

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
    public const int MaxResultRows = 20;

    /// <summary>
    /// Builds the <c>get-document-relations</c> MAF agent skill (inline). Lives in
    /// core's tool stack (not in any business module) because <see cref="DocumentRelation"/>
    /// is a Core aggregate and the link semantics are owner-agnostic.
    /// </summary>
    public virtual AgentInlineSkill CreateSkill()
    {
        return new AgentInlineSkill(
            name: "get-document-relations",
            description:
                "Discover documents linked to an anchor document via the DocumentRelation graph " +
                "(both user-confirmed manual links and AI-suggested links). Use when the user's " +
                "question implies cross-document evidence — payments tied to a contract, attachments " +
                "of an invoice, amendments of a master agreement.",
            instructions:
                "Use this skill when the question implies cross-document evidence — e.g. \"has this " +
                "contract been paid?\", \"what documents reference this invoice?\", \"any amendments to " +
                "this agreement?\".\n\n" +
                "Steps:\n" +
                "1. Take the anchor document ID from the current conversation context or a prior tool result.\n" +
                "2. Call the `invoke` script with that document ID. The script returns up to " +
                $"{MaxResultRows} related documents, manual links first, then AI-suggested links by " +
                "confidence descending.\n" +
                "3. To read the actual content of related documents, call the `" + ChatToolNames.SearchPaperbaseDocuments + "` " +
                "tool with `documentIds` set to the returned `relatedDocumentId` values. The relations " +
                "graph is a pointer; vector search is how you read the body.\n\n" +
                "The result is structural (IDs, enum source labels, confidence numbers) — no user-derived " +
                "free text, so no `<field>` wrapping is needed.")
            .AddScript("invoke", InvokeAsync);
    }

    /// <summary>
    /// Script body for the <c>invoke</c> script of the <c>get-document-relations</c> skill.
    /// Resolves services per call via <see cref="IServiceProvider"/> so the body sees the
    /// correct request-scoped <see cref="ICurrentTenant"/> / <see cref="IAuthorizationService"/>.
    /// </summary>
    /// <remarks>
    /// fail-closed safety contract — see <c>.claude/rules/doc-chat-anti-patterns.md</c>
    /// reverse example C: explicit <see cref="PaperbasePermissions.Documents.Default"/>
    /// permission check, explicit tenant predicate (never rely on ambient ABP <c>DataFilter</c>),
    /// hard <see cref="MaxResultRows"/> upper bound.
    /// </remarks>
    public virtual async Task<string> InvokeAsync(
        [Description("Anchor document ID to look up relations for. Returns documents linked to it (in either direction).")]
        Guid documentId,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        var authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
        await authorizationService.CheckAsync(PaperbasePermissions.Documents.Default);

        var repository = serviceProvider.GetRequiredService<IDocumentRelationRepository>();
        var executer = serviceProvider.GetRequiredService<IAsyncQueryableExecuter>();
        var currentTenant = serviceProvider.GetRequiredService<ICurrentTenant>();

        var queryable = await repository.GetQueryableAsync();

        // Explicit tenant predicate — defense-in-depth against any code path that
        // disables ABP's ambient DataFilter (background jobs, non-HTTP contexts).
        var tenantId = currentTenant.Id;
        queryable = tenantId.HasValue
            ? queryable.Where(r => r.TenantId == tenantId)
            : queryable.Where(r => r.TenantId == null);

        // Bidirectional lookup: relations are symmetric in semantics even though
        // SourceDocumentId / TargetDocumentId encode a directed edge — the user is
        // equally interested in "what does X link to" and "what links to X".
        queryable = queryable.Where(r =>
            r.SourceDocumentId == documentId || r.TargetDocumentId == documentId);

        // Manual relations come first (user-confirmed = highest signal); within each bucket,
        // newest first. Source enum: Manual=1, AiSuggested=2, so OrderBy(Source) naturally
        // puts Manual first. Take(N) bounds context-window cost.
        var relations = await executer.ToListAsync(
            queryable
                .OrderBy(r => r.Source)
                .ThenByDescending(r => r.CreationTime)
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
                // Description is user-controlled (set by manual relation creation OR by
                // the AI inference workflow that extracted it from user documents).
                // Wrap to keep indirect prompt-injection content inside <field>…</field>.
                description = PromptBoundary.WrapField(r.Description),
                source = r.Source.ToString(),   // "Manual" / "AiSuggested"
            })
        };

        return JsonSerializer.Serialize(payload);
    }
}

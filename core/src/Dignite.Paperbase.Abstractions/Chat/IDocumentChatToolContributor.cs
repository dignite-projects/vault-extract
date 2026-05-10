using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Abstractions.Chat;

/// <summary>
/// Extension point that lets business modules contribute <see cref="AIFunction"/> tools
/// to document chat conversations.
///
/// <para>
/// <strong>In-process usage:</strong> implement this interface in a business module's
/// Application layer and register it via ABP's auto-DI
/// (<see cref="Volo.Abp.DependencyInjection.ITransientDependency"/>).
/// <see cref="DocumentChatAppService"/> collects every registered contributor on every
/// turn and adds the returned <see cref="AIFunction"/> instances to the active
/// <c>ChatClientAgent</c>'s tool list. Tool selection is the LLM's job; the AppService
/// no longer filters contributors by document type (Issue #100 — cross-document reasoning
/// requires multiple modules' tools to be co-resident).
/// </para>
///
/// <para>
/// <strong>fail-closed safety:</strong> tool bodies MUST enforce
/// <c>IAuthorizationService.CheckAsync</c> + an explicit <c>TenantId</c> predicate +
/// a hard result-set cap (<c>Take(N)</c>), and tool descriptions MUST be compile-time
/// constants that never embed user input. See
/// <c>.claude/rules/doc-chat-anti-patterns.md</c> reverse example C.
/// </para>
///
/// <para>
/// <strong>MCP / cross-process expansion (future, backlog):</strong>
/// contributors receive an <see cref="IDocumentChatToolFactory"/> so all in-process tools
/// use the same audit, logging, and metrics behavior. Future cross-process/MCP tools should
/// either be adapted through this factory or provide equivalent instrumentation at the bridge.
/// </para>
/// </summary>
public interface IDocumentChatToolContributor
{
    /// <summary>
    /// Informational hint describing the document type this contributor primarily targets
    /// (<c>&lt;owner-module&gt;.&lt;sub-type&gt;</c>). <strong>Not a router</strong>: tools
    /// are exposed every turn regardless of the active conversation. Use this property for
    /// debugging / telemetry / future trimming policies, not for access control.
    /// </summary>
    string DocumentTypeCode { get; }

    /// <summary>
    /// Returns the <see cref="AIFunction"/> tools to add to the chat agent for the given
    /// conversation context. Called once per turn; the result is merged with the built-in
    /// <c>search_paperbase_documents</c> RAG tool.
    /// </summary>
    IEnumerable<AIFunction> ContributeTools(
        DocumentChatToolContext ctx,
        IDocumentChatToolFactory toolFactory);
}

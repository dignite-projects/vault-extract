using System;

namespace Dignite.Paperbase.Abstractions.Chat;

/// <summary>
/// Contextual information passed to <see cref="IDocumentChatToolContributor.ContributeTools"/>
/// so contributors can scope their tool implementations to the right tenant.
/// </summary>
public sealed class DocumentChatToolContext
{
    /// <summary>
    /// Optional document type hint. Issue #100 stopped pinning this on the conversation;
    /// the AppService now passes <c>null</c> by default. Reserved for future use (e.g. when
    /// a contributor wants to know the anchor document's type for telemetry tagging) —
    /// contributors MUST NOT use this for access control, since it carries no authorization
    /// signal.
    /// </summary>
    public string? DocumentTypeCode { get; init; }

    /// <summary>Tenant of the conversation. Use this to scope data access inside tool implementations — the only field on this context that carries authorization weight.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Conversation identifier. Useful for per-turn audit logging inside tools.</summary>
    public Guid ConversationId { get; init; }

    /// <summary>
    /// Anchor document the conversation was started from (if any). Treat as a hint
    /// (e.g. for telemetry); not a hard scope — the LLM is free to query other documents.
    /// </summary>
    public Guid? DocumentId { get; init; }

    /// <summary>User that initiated the current chat turn.</summary>
    public Guid? UserId { get; init; }
}

using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Input for creating a chat conversation. Issue #100 dropped <c>DocumentTypeCode</c> /
/// <c>TopK</c> / <c>MinScore</c>: scope is now decided per-turn by the model based on
/// question intent, not pinned at conversation creation. <see cref="DocumentId"/> is
/// retained as an **anchor / grouping key** — it surfaces a per-turn hint into the
/// system prompt and lets the UI list "conversations started on this document", but it
/// does **not** constrain RAG retrieval.
/// </summary>
public class CreateChatConversationInput
{
    [StringLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Optional anchor document the user was viewing when starting the conversation.
    /// Surfaces as a per-turn anchor hint in the system prompt; never a hard scope.
    /// </summary>
    public Guid? DocumentId { get; set; }
}

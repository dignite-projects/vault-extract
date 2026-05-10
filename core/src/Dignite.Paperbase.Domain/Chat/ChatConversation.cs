using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Paperbase.Chat;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;

namespace Dignite.Paperbase.Chat;

public class ChatConversation : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }
    public virtual string Title { get; private set; } = default!;

    /// <summary>
    /// The document the user was viewing when they started this conversation.
    /// Pure UI/anchor metadata — used to (a) group conversations on a document detail
    /// page and (b) feed a per-turn anchor hint into the system prompt. **Not** a hard
    /// retrieval scope: the model is free to (and encouraged to) cross-document search
    /// regardless of this value. See Issue #100 for the rationale.
    /// </summary>
    public virtual Guid? DocumentId { get; private set; }

    public virtual ICollection<ChatMessage> Messages { get; protected set; }

    protected ChatConversation()
    {
        Messages = new List<ChatMessage>();
    }

    public ChatConversation(
        Guid id,
        Guid? tenantId,
        string title,
        Guid? documentId)
        : base(id)
    {
        TenantId = tenantId;
        Title = Check.NotNullOrWhiteSpace(title, nameof(title), maxLength: ChatConsts.MaxTitleLength);
        DocumentId = documentId;
        Messages = new List<ChatMessage>();
        // ConcurrencyStamp is owned by ABP. Manually rotating it here would conflict
        // with AbpDbContext.UpdateConcurrencyStamp, which sets OriginalValue from the
        // entity's current ConcurrencyStamp at save time — pre-rotated entities would
        // produce a WHERE clause that never matches the persisted row.
    }

    public virtual void Rename(string title)
    {
        Title = Check.NotNullOrWhiteSpace(title, nameof(title), maxLength: ChatConsts.MaxTitleLength);
    }

    public virtual ChatMessage AppendUserMessage(IClock clock, Guid messageId, string content, Guid clientTurnId)
    {
        if (Messages.Any(m => m.ClientTurnId == clientTurnId))
            throw new BusinessException(PaperbaseErrorCodes.DuplicateClientTurnId);

        var message = new ChatMessage(
            messageId,
            Id,
            ChatMessageRole.User,
            content,
            citationsJson: null,
            isDegraded: false,
            clientTurnId,
            clock.Now);

        Messages.Add(message);
        return message;
    }

    public virtual ChatMessage AppendAssistantMessage(
        IClock clock,
        Guid messageId,
        string content,
        string? citationsJson,
        bool isDegraded)
    {
        var message = new ChatMessage(
            messageId,
            Id,
            ChatMessageRole.Assistant,
            content,
            citationsJson,
            isDegraded,
            clientTurnId: null,
            clock.Now);

        Messages.Add(message);
        return message;
    }

}

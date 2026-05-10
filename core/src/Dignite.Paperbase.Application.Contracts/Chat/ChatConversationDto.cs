using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Chat;

public class ChatConversationDto : FullAuditedEntityDto<Guid>
{
    public Guid? TenantId { get; set; }

    public string Title { get; set; } = default!;

    /// <summary>
    /// Anchor document the user was viewing when starting the conversation. Per-turn
    /// retrieval scope is decided by the model from question intent, not from this
    /// value. See <see cref="CreateChatConversationInput.DocumentId"/>.
    /// </summary>
    public Guid? DocumentId { get; set; }
}

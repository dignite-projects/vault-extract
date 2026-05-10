using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Chat;

public class ChatConversationListItemDto : EntityDto<Guid>
{
    public string Title { get; set; } = default!;

    /// <summary>
    /// Anchor document this conversation was started on. Used by the UI to filter
    /// "conversations on document X". See <see cref="ChatConversationDto.DocumentId"/>.
    /// </summary>
    public Guid? DocumentId { get; set; }

    public DateTime CreationTime { get; set; }
}

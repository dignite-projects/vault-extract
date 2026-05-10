namespace Dignite.Paperbase.Chat;

public enum ChatTurnDeltaKind
{
    /// <summary>
    /// An incremental text chunk. <see cref="ChatTurnDeltaDto.Text"/> carries the delta
    /// (not cumulative). Clients concatenate these to build the full answer.
    /// </summary>
    PartialText,

    /// <summary>
    /// Terminal event. Stream is complete; the turn was persisted.
    /// <see cref="ChatTurnDeltaDto.UserMessageId"/>, <see cref="ChatTurnDeltaDto.AssistantMessageId"/>,
    /// and <see cref="ChatTurnDeltaDto.Citations"/> are populated.
    /// </summary>
    Done,

    /// <summary>
    /// Terminal event. An unrecoverable error occurred during streaming.
    /// <see cref="ChatTurnDeltaDto.ErrorMessage"/> carries a safe client-facing message.
    /// No further events follow.
    /// </summary>
    Error,

    /// <summary>
    /// Issue #116: model decided to invoke a tool. Populated fields:
    /// <see cref="ChatTurnDeltaDto.ToolName"/>,
    /// <see cref="ChatTurnDeltaDto.ToolCallId"/>,
    /// <see cref="ChatTurnDeltaDto.ProgressDescription"/>.
    /// Lets the UI render an in-progress card (e.g. "▸ 正在按甲方 'X 公司' 查找合同")
    /// instead of a black screen during multi-step tool reasoning.
    /// </summary>
    ToolCallStarted,

    /// <summary>
    /// Issue #116: a tool returned (or threw). Correlates with the matching
    /// <see cref="ToolCallStarted"/> via <see cref="ChatTurnDeltaDto.ToolCallId"/>.
    /// Populated: <see cref="ChatTurnDeltaDto.ToolName"/>,
    /// <see cref="ChatTurnDeltaDto.ToolCallId"/>,
    /// <see cref="ChatTurnDeltaDto.ElapsedMs"/>,
    /// <see cref="ChatTurnDeltaDto.ToolCallSucceeded"/>.
    /// </summary>
    ToolCallCompleted
}

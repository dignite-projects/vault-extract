using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.HttpApi.Chat;

[Area("paperbase")]
[Route("api/paperbase/document-chat")]
public class DocumentChatController : PaperbaseController, IDocumentChatAppService
{
    // Issue #116 FE half: the SSE handler below bypasses ABP's MVC pipeline (it
    // writes the response stream directly), so it never picks up ABP's per-request
    // JSON options. Default System.Text.Json serializes PascalCase property names,
    // which would force the Angular consumer to special-case Kind/ToolName/... vs
    // the camelCase the rest of the proxy types use. JsonSerializerDefaults.Web
    // gives camelCase + the same enum-as-number convention every other endpoint
    // in this controller already speaks.
    private static readonly JsonSerializerOptions SseSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IDocumentChatAppService _documentChatAppService;

    public DocumentChatController(IDocumentChatAppService documentChatAppService)
    {
        _documentChatAppService = documentChatAppService;
    }

    [HttpPost("conversations")]
    public virtual Task<ChatConversationDto> CreateConversationAsync(
        [FromBody] CreateChatConversationInput input)
    {
        return _documentChatAppService.CreateConversationAsync(input);
    }

    [HttpGet("conversations")]
    public virtual Task<PagedResultDto<ChatConversationListItemDto>> GetConversationListAsync(
        [FromQuery] GetChatConversationListInput input)
    {
        return _documentChatAppService.GetConversationListAsync(input);
    }

    [HttpGet("conversations/{conversationId}")]
    public virtual Task<ChatConversationDto> GetConversationAsync(Guid conversationId)
    {
        return _documentChatAppService.GetConversationAsync(conversationId);
    }

    [HttpDelete("conversations/{conversationId}")]
    public virtual Task DeleteConversationAsync(Guid conversationId)
    {
        return _documentChatAppService.DeleteConversationAsync(conversationId);
    }

    // SSE streaming is handled by StreamAsync below.
    // This stub satisfies the C# interface contract; [RemoteService(false)] on the
    // interface method prevents ABP from generating an auto-API route for it.
    [NonAction]
    public virtual IAsyncEnumerable<ChatTurnDeltaDto> SendMessageStreamingAsync(
        Guid conversationId,
        SendChatMessageInput input,
        CancellationToken cancellationToken = default)
        => _documentChatAppService.SendMessageStreamingAsync(conversationId, input, cancellationToken);

    /// <summary>
    /// Streams the response for a new chat turn as Server-Sent Events.
    /// </summary>
    [Authorize]
    [HttpPost("conversations/{conversationId}/messages/stream")]
    public virtual async Task StreamAsync(
        Guid conversationId,
        [FromBody] SendChatMessageInput input,
        CancellationToken cancellationToken)
    {
        Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        // Disable proxy/CDN buffering so chunks reach the client immediately.
        Response.Headers["X-Accel-Buffering"] = "no";

        await foreach (var delta in _documentChatAppService.SendMessageStreamingAsync(
            conversationId, input, cancellationToken))
        {
            var json = JsonSerializer.Serialize(delta, SseSerializerOptions);
            await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    [HttpPost("conversations/{conversationId}/messages")]
    public virtual Task<ChatTurnResultDto> SendMessageAsync(
        Guid conversationId,
        [FromBody] SendChatMessageInput input)
    {
        return _documentChatAppService.SendMessageAsync(conversationId, input);
    }

    [HttpGet("conversations/{conversationId}/messages")]
    public virtual Task<PagedResultDto<ChatMessageDto>> GetMessageListAsync(
        Guid conversationId,
        [FromQuery] GetChatMessageListInput input)
    {
        return _documentChatAppService.GetMessageListAsync(conversationId, input);
    }
}

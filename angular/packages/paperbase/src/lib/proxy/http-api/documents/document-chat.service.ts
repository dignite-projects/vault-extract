import { AuthService, EnvironmentService, RestService, Rest } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import type {
  ChatConversationDto,
  ChatConversationListItemDto,
  ChatMessageDto,
  ChatTurnDeltaDto,
  ChatTurnResultDto,
  CreateChatConversationInput,
  GetChatConversationListInput,
  GetChatMessageListInput,
  SendChatMessageInput,
} from '../../documents/chat/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentChatService {
  private restService = inject(RestService);
  // Issue #116 FE half: streaming is hand-rolled (fetch + ReadableStream) because
  // ABP's RestService is request/response shaped. EnvironmentService gives us the
  // API base URL the rest of the proxy uses; AuthService gives us the same bearer
  // token RestService would attach.
  private environmentService = inject(EnvironmentService);
  private authService = inject(AuthService);
  apiName = 'Default';


  createConversation = (input: CreateChatConversationInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ChatConversationDto>({
      method: 'POST',
      url: '/api/paperbase/document-chat/conversations',
      body: input,
    },
    { apiName: this.apiName,...config });


  deleteConversation = (conversationId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/paperbase/document-chat/conversations/${conversationId}`,
    },
    { apiName: this.apiName,...config });


  getConversation = (conversationId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ChatConversationDto>({
      method: 'GET',
      url: `/api/paperbase/document-chat/conversations/${conversationId}`,
    },
    { apiName: this.apiName,...config });


  getConversationList = (input: GetChatConversationListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, PagedResultDto<ChatConversationListItemDto>>({
      method: 'GET',
      url: '/api/paperbase/document-chat/conversations',
      params: { documentId: input.documentId, sorting: input.sorting, skipCount: input.skipCount, maxResultCount: input.maxResultCount },
    },
    { apiName: this.apiName,...config });


  getMessageList = (conversationId: string, input: GetChatMessageListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, PagedResultDto<ChatMessageDto>>({
      method: 'GET',
      url: `/api/paperbase/document-chat/conversations/${conversationId}/messages`,
      params: { sorting: input.sorting, skipCount: input.skipCount, maxResultCount: input.maxResultCount },
    },
    { apiName: this.apiName,...config });


  sendMessage = (conversationId: string, input: SendChatMessageInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ChatTurnResultDto>({
      method: 'POST',
      url: `/api/paperbase/document-chat/conversations/${conversationId}/messages`,
      body: input,
    },
    { apiName: this.apiName,...config });

  /**
   * Issue #116: streams the per-turn deltas (PartialText + ToolCallStarted /
   * ToolCallCompleted + Done / Error) from the SSE endpoint at
   * `POST /api/paperbase/document-chat/conversations/{id}/messages/stream`.
   *
   * Why fetch + ReadableStream instead of `EventSource`:
   * - `EventSource` is GET-only; the backend takes the message body as POST
   * - `EventSource` cannot set `Authorization` headers without browser polyfills
   *
   * The Observable's teardown aborts the fetch, so unsubscribing mid-turn (e.g.
   * the user navigates away) cancels the underlying HTTP request and the backend
   * sees `OperationCanceledException` — which `FillStreamingChannelAsync` already
   * handles by discarding the partial answer instead of persisting it.
   */
  sendMessageStream(conversationId: string, input: SendChatMessageInput): Observable<ChatTurnDeltaDto> {
    return new Observable<ChatTurnDeltaDto>(subscriber => {
      const controller = new AbortController();
      const baseUrl = this.environmentService.getApiUrl('default');
      const url = `${baseUrl}/api/paperbase/document-chat/conversations/${conversationId}/messages/stream`;
      const token = this.authService.getAccessToken();

      fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'text/event-stream',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify(input),
        signal: controller.signal,
      })
        .then(async response => {
          if (!response.ok || !response.body) {
            subscriber.error(new Error(`SSE stream failed (HTTP ${response.status})`));
            return;
          }

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          try {
            while (true) {
              const { value, done } = await reader.read();
              if (done) break;
              buffer += decoder.decode(value, { stream: true });

              // SSE events are separated by `\n\n`. Within an event, lines
              // starting with `data: ` carry the JSON payload (possibly across
              // multiple lines per the spec — concatenated with `\n`).
              let separatorIdx: number;
              while ((separatorIdx = buffer.indexOf('\n\n')) >= 0) {
                const rawEvent = buffer.slice(0, separatorIdx);
                buffer = buffer.slice(separatorIdx + 2);

                const dataPayload = rawEvent
                  .split('\n')
                  .filter(line => line.startsWith('data:'))
                  .map(line => line.replace(/^data:\s?/, ''))
                  .join('\n');

                if (!dataPayload) continue;

                try {
                  subscriber.next(JSON.parse(dataPayload) as ChatTurnDeltaDto);
                } catch (parseErr) {
                  subscriber.error(parseErr);
                  return;
                }
              }
            }
            subscriber.complete();
          } catch (err) {
            // AbortController-driven cancellation surfaces as DOMException 'AbortError'
            // — that is normal teardown, not a stream failure.
            if (controller.signal.aborted) {
              subscriber.complete();
            } else {
              subscriber.error(err);
            }
          }
        })
        .catch(err => {
          if (!controller.signal.aborted) {
            subscriber.error(err);
          }
        });

      return () => controller.abort();
    });
  }
}

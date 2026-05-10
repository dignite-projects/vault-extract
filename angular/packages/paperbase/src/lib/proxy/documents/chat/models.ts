import type { EntityDto, FullAuditedEntityDto, PagedAndSortedResultRequestDto } from '@abp/ng.core';
import type { ChatMessageRole } from './chat-message-role.enum';

export interface ChatCitationDto {
  documentId?: string;
  pageNumber?: number | null;
  chunkIndex?: number | null;
  snippet?: string;
  sourceName?: string;
}

export interface ChatConversationDto extends FullAuditedEntityDto<string> {
  tenantId?: string | null;
  title?: string;
  documentId?: string | null;
  documentTypeCode?: string | null;
  topK?: number | null;
  minScore?: number | null;
}

export interface ChatConversationListItemDto extends EntityDto<string> {
  title?: string;
  documentId?: string | null;
  documentTypeCode?: string | null;
  creationTime?: string;
}

export interface ChatMessageDto extends EntityDto<string> {
  conversationId?: string;
  role?: ChatMessageRole;
  content?: string;
  citationsJson?: string | null;
  clientTurnId?: string | null;
  creationTime?: string;
}

export interface ChatTurnResultDto {
  userMessageId?: string;
  assistantMessageId?: string;
  answer?: string;
  citations?: ChatCitationDto[];
  isDegraded?: boolean;
  // Issue #99: surfaces grounding source so the UI can distinguish "answered
  // via vector search" / "answered via structured tools" / mixed / none.
  groundingSource?: GroundingSource;
}

// Issue #99 / #100: categorises which kinds of tools the model invoked in a turn.
// Numeric values must match the .NET enum order in
// core/src/Dignite.Paperbase.Application.Contracts/Chat/GroundingSource.cs.
export enum GroundingSource {
  None = 0,
  Vector = 1,
  Structured = 2,
  Mixed = 3,
}

// Issue #116: SSE delta event shape streamed from the
// `POST /api/paperbase/document-chat/conversations/{id}/messages/stream` endpoint.
// Numeric `kind` values must match the .NET enum order in
// core/src/Dignite.Paperbase.Application.Contracts/Chat/ChatTurnDeltaKind.cs.
export enum ChatTurnDeltaKind {
  PartialText = 0,
  Done = 1,
  Error = 2,
  ToolCallStarted = 3,
  ToolCallCompleted = 4,
}

export interface ChatTurnDeltaDto {
  kind: ChatTurnDeltaKind;
  // PartialText
  text?: string;
  // Done
  citations?: ChatCitationDto[];
  userMessageId?: string;
  assistantMessageId?: string;
  isDegraded?: boolean;
  groundingSource?: GroundingSource;
  // Error
  errorMessage?: string;
  // ToolCallStarted / ToolCallCompleted (Issue #116)
  toolName?: string;
  toolCallId?: string;
  // ToolCallStarted only — sanitised, user-facing label produced by the tool
  // contributor's progress describer (see `.claude/rules/doc-chat-anti-patterns.md`
  // reverse example C #4 / Issue #130: never echoes raw model arguments).
  progressDescription?: string;
  // ToolCallCompleted only
  elapsedMs?: number;
  toolCallSucceeded?: boolean;
}

export interface CreateChatConversationInput {
  title?: string | null;
  documentId?: string | null;
  documentTypeCode?: string | null;
  topK?: number | null;
  minScore?: number | null;
}

export interface GetChatConversationListInput extends PagedAndSortedResultRequestDto {
  documentId?: string | null;
}

export interface GetChatMessageListInput extends PagedAndSortedResultRequestDto {
}

export interface SendChatMessageInput {
  message: string;
  clientTurnId: string;
}

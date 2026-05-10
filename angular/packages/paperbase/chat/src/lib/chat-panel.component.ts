import { CommonModule } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  ElementRef,
  OnChanges,
  OnInit,
  SimpleChanges,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SafeHtml, DomSanitizer } from '@angular/platform-browser';
import { RouterModule } from '@angular/router';
import { LocalizationPipe, LocalizationService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { finalize } from 'rxjs';
import { marked } from 'marked';
import {
  ChatMessageRole,
  ChatCitationDto,
  ChatConversationDto,
  ChatConversationListItemDto,
  ChatMessageDto,
  ChatTurnDeltaDto,
  ChatTurnDeltaKind,
  CreateChatConversationInput,
  DocumentDto,
  DocumentChatService,
  DocumentService,
} from '@dignite/paperbase';

interface ChatMessageView {
  id: string;
  role: ChatMessageRole;
  content: string;
  creationTime?: string;
  citations: ChatCitationDto[];
  isDegraded?: boolean;
  isPending?: boolean;
  isError?: boolean;
  // Issue #116: per-message ordered list of tool-call events the agent fired
  // while producing this assistant turn. Populated only on streaming send;
  // sync replay (idempotent path) and historical message load leave it empty.
  toolEvents?: ToolCallEventView[];
}

// Issue #116: UI projection of ToolCallStarted/Completed pairs. Started events
// open the entry; the matching Completed flips state to success/failure and
// fills elapsedMs. Order is the order events arrive on the SSE channel — that
// matches the order the LLM invoked the tools, which is what users want to read.
interface ToolCallEventView {
  toolCallId: string;
  toolName: string;
  description: string;
  status: 'pending' | 'success' | 'failure';
  elapsedMs?: number;
}

interface SelectedCitation {
  messageId: string;
  citationIndex: number;
  citation: ChatCitationDto;
}

export type ChatPanelMode = 'full' | 'panel';

@Component({
  selector: 'lib-chat-panel',
  templateUrl: './chat-panel.component.html',
  styleUrls: ['./chat-panel.component.scss'],
  imports: [CommonModule, FormsModule, RouterModule, LocalizationPipe],
})
export class ChatPanelComponent implements OnInit, OnChanges, AfterViewChecked {
  private readonly chatService = inject(DocumentChatService);
  private readonly documentService = inject(DocumentService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly localization = inject(LocalizationService);

  readonly mode = input<ChatPanelMode>('full');
  // In `panel` mode the panel auto-scopes new conversations to this `documentId`
  // and filters the conversation list to it. In `full` mode `/chat` should pass
  // nothing — independent Chat starts unscoped and the user's question (and
  // backend tools) decide which documents are involved.
  readonly documentId = input<string | undefined>(undefined);
  readonly documentTypeCode = input<string | undefined>(undefined);

  readonly ChatMessageRole = ChatMessageRole;

  conversations = signal<ChatConversationListItemDto[]>([]);
  activeConversation = signal<ChatConversationDto | null>(null);
  messages = signal<ChatMessageView[]>([]);
  sourceDocument = signal<DocumentDto | null>(null);
  selectedCitation = signal<SelectedCitation | null>(null);

  message = signal('');

  isLoadingConversations = signal(false);
  isLoadingMessages = signal(false);
  isCreating = signal(false);
  isSending = signal(false);
  isLoadingSource = signal(false);
  sourceError = signal<string | null>(null);

  isPanelMode = computed(() => this.mode() === 'panel');
  // Panel-mode shows only conversations scoped to the current document; in full
  // mode the user sees everything (including unscoped + per-doc-type chats).
  visibleConversations = computed<ChatConversationListItemDto[]>(() => {
    if (this.isPanelMode()) {
      const docId = this.documentId();
      if (!docId) return [];
      return this.conversations().filter(c => c.documentId === docId);
    }
    return this.conversations();
  });

  activeConversationId = computed(() => this.activeConversation()?.id ?? null);
  canSend = computed(() => !!this.message().trim() && !this.isSending());
  // Markdown is rendered (rather than displayed as `<pre>` text) because the project
  // is AI-first and the persisted Markdown is the single canonical text artifact —
  // headings/lists/tables carry semantic structure that helps the user scan.
  // marked is configured with `mangle:false` and `headerIds:false` to keep output
  // deterministic and free of injected anchor IDs.
  markdownHtml = computed<SafeHtml | null>(() => {
    const markdown = this.sourceDocument()?.markdown;
    if (!markdown) return null;
    const html = marked.parse(markdown, { async: false, gfm: true }) as string;
    return this.sanitizer.bypassSecurityTrustHtml(html);
  });
  // The snippet may not exist in the rendered DOM (LLM rephrased the chunk, OCR
  // changed between embedding and view). The signal is recomputed every time the
  // citation changes; the actual `<mark>` wrapping happens after view-check via
  // tryHighlightSnippet().
  snippetMissing = signal(false);
  private readonly markdownContainer = viewChild<ElementRef<HTMLElement>>('markdownContainer');
  private lastHighlightedSnippet: string | null = null;
  private lastHighlightedDocumentId: string | null = null;
  private sourceRequestId = 0;

  ngOnInit(): void {
    this.loadConversations();
  }

  ngOnChanges(changes: SimpleChanges): void {
    // In panel mode, the host (document-detail) owns the documentId and may swap
    // it without re-creating the component (e.g. user navigates to a sibling
    // document). Reload + re-scope when the input changes after init.
    if (changes['documentId'] && !changes['documentId'].firstChange && this.isPanelMode()) {
      this.activeConversation.set(null);
      this.messages.set([]);
      this.selectedCitation.set(null);
      this.loadConversations();
    }
  }

  ngAfterViewChecked(): void {
    this.tryHighlightSnippet();
  }

  loadConversations(afterLoad?: () => void): void {
    this.isLoadingConversations.set(true);
    this.chatService
      .getConversationList({
        documentId: this.isPanelMode() ? this.documentId() ?? null : null,
        maxResultCount: 50,
        skipCount: 0,
        sorting: 'CreationTime DESC',
      })
      .pipe(finalize(() => this.isLoadingConversations.set(false)))
      .subscribe({
        next: result => {
          this.conversations.set(result.items ?? []);
          afterLoad?.();
        },
        error: () => this.toaster.error('::DocumentChat:LoadFailed', '::Error'),
      });
  }

  selectConversation(conversationId?: string): void {
    if (!conversationId) return;

    this.isLoadingMessages.set(true);
    this.chatService.getConversation(conversationId).subscribe({
      next: conversation => {
        this.activeConversation.set(conversation);
        this.selectedCitation.set(null);
        this.sourceError.set(null);
        if (!this.isPanelMode() && conversation.documentId) {
          this.loadSourceDocument(conversation.documentId);
        } else {
          this.sourceRequestId++;
          this.isLoadingSource.set(false);
          this.sourceDocument.set(null);
        }
        this.chatService
          .getMessageList(conversationId, {
            maxResultCount: 100,
            skipCount: 0,
            sorting: 'CreationTime ASC',
          })
          .pipe(finalize(() => this.isLoadingMessages.set(false)))
          .subscribe({
            next: result => this.messages.set((result.items ?? []).map(m => this.toMessageView(m))),
            error: () => this.toaster.error('::DocumentChat:LoadFailed', '::Error'),
          });
      },
      error: () => {
        this.isLoadingMessages.set(false);
        this.toaster.error('::DocumentChat:LoadFailed', '::Error');
      },
    });
  }

  startDraft(): void {
    this.activeConversation.set(null);
    this.messages.set([]);
    this.selectedCitation.set(null);
    this.sourceRequestId++;
    this.isLoadingSource.set(false);
    this.sourceDocument.set(null);
    this.sourceError.set(null);
  }

  deleteConversation(conversation: ChatConversationListItemDto, event: Event): void {
    event.stopPropagation();
    if (!conversation.id) return;

    this.confirmation
      .warn('::DocumentChat:AreYouSureToDelete', '::AreYouSure')
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;

        this.chatService.deleteConversation(conversation.id!).subscribe({
          next: () => {
            if (this.activeConversationId() === conversation.id) {
              this.activeConversation.set(null);
              this.messages.set([]);
            }
            this.loadConversations();
          },
          error: () => this.toaster.error('::DocumentChat:DeleteFailed', '::Error'),
        });
      });
  }

  sendMessage(): void {
    const text = this.message().trim();
    if (!text || this.isSending()) return;

    const active = this.activeConversation();
    if (!active?.id) {
      this.createAndOpen(
        {
          documentId: this.isPanelMode() ? this.documentId() ?? null : null,
          documentTypeCode: null,
        },
        created => this.sendToConversation(created, text)
      );
      return;
    }

    this.sendToConversation(active, text);
  }

  onMessageKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      this.sendMessage();
    }
  }

  sourceLabel(conversation: ChatConversationDto | ChatConversationListItemDto): string {
    if (conversation.documentTypeCode) return conversation.documentTypeCode;
    if (conversation.documentId) {
      return this.localization.instant({
        key: '::DocumentChat:Scope:Document',
        defaultValue: `doc:${conversation.documentId.slice(0, 8)}`,
      }, conversation.documentId.slice(0, 8));
    }
    return this.localization.instant({ key: '::DocumentChat:Scope:Global', defaultValue: 'global' });
  }

  selectCitation(messageId: string, citationIndex: number, citation: ChatCitationDto): void {
    this.selectedCitation.set({ messageId, citationIndex, citation });

    if (this.isPanelMode()) {
      // Panel mode is embedded inside the source document detail page; the
      // citation already points back to that document, so the duplicate
      // source-pane preview is suppressed — selection is purely visual.
      return;
    }

    if (!citation.documentId) {
      this.sourceRequestId++;
      this.isLoadingSource.set(false);
      this.sourceDocument.set(null);
      this.sourceError.set('::DocumentChat:SourceMissingDocumentId');
      return;
    }

    this.loadSourceDocument(citation.documentId);
  }

  isSelectedCitation(messageId: string, citationIndex: number): boolean {
    const selected = this.selectedCitation();
    return selected?.messageId === messageId && selected.citationIndex === citationIndex;
  }

  citationKey(messageId: string, citationIndex: number): string {
    return `${messageId}::${citationIndex}`;
  }

  sourceTitle(document: DocumentDto): string {
    return document.fileOrigin?.originalFileName || document.originalFileBlobName || document.id || 'Source document';
  }

  private createAndOpen(
    input: CreateChatConversationInput,
    afterCreate?: (conversation: ChatConversationDto) => void
  ): void {
    this.isCreating.set(true);
    this.chatService
      .createConversation(input)
      .pipe(finalize(() => this.isCreating.set(false)))
      .subscribe({
        next: conversation => {
          this.activeConversation.set(conversation);
          this.messages.set([]);
          this.selectedCitation.set(null);
          if (!this.isPanelMode() && conversation.documentId) {
            this.loadSourceDocument(conversation.documentId);
          } else {
            this.sourceRequestId++;
            this.isLoadingSource.set(false);
            this.sourceDocument.set(null);
          }
          this.loadConversations();
          afterCreate?.(conversation);
        },
        error: () => this.toaster.error('::DocumentChat:CreateFailed', '::Error'),
      });
  }

  /**
   * Issue #116: streams the turn so the user sees tool-call progress + text
   * deltas in real time instead of staring at a `Thinking…` spinner during the
   * 5–10s the multi-step tool reasoning takes. Internally consumes SSE via
   * {@link DocumentChatService.sendMessageStream}.
   *
   * Lifecycle of the pending assistant message:
   *  - On send: insert a row with `isPending=true`, empty content, empty toolEvents
   *  - On `ToolCallStarted`: append an entry to that row's `toolEvents` (status=pending)
   *  - On `ToolCallCompleted`: flip the matching entry's status (success/failure) +
   *    fill elapsedMs (matched by toolCallId so out-of-order completions still bind)
   *  - On `PartialText`: clear `isPending` (we have content now) + append text
   *  - On `Done`: replace pending id with assistant id, set citations + isDegraded
   *  - On `Error`: flip to error state
   */
  private sendToConversation(conversation: ChatConversationDto, text: string): void {
    if (!conversation.id) return;

    const clientTurnId = crypto.randomUUID();
    const pendingAssistantId = `pending-${clientTurnId}`;
    this.message.set('');
    this.messages.update(items => [
      ...items,
      {
        id: clientTurnId,
        role: ChatMessageRole.User,
        content: text,
        citations: [],
      },
      {
        id: pendingAssistantId,
        role: ChatMessageRole.Assistant,
        content: '',
        citations: [],
        isPending: true,
        toolEvents: [],
      },
    ]);

    this.isSending.set(true);
    this.chatService
      .sendMessageStream(conversation.id, { message: text, clientTurnId })
      .pipe(finalize(() => this.isSending.set(false)))
      .subscribe({
        next: delta => this.applyDelta(pendingAssistantId, delta),
        error: () => {
          this.messages.update(items =>
            items.map(item =>
              item.id === pendingAssistantId
                ? {
                    ...item,
                    content: 'DocumentChat:SendFailed',
                    isPending: false,
                    isError: true,
                  }
                : item
            )
          );
          this.toaster.error('::DocumentChat:SendFailed', '::Error');
        },
        complete: () => this.loadConversations(),
      });
  }

  /**
   * Mutates the pending assistant row in place based on a single SSE delta.
   * Kept alongside {@link sendToConversation} so the streaming wiring stays
   * grokkable — the kind/branch table here is the spec for the FE half of
   * `ChatTurnDeltaKind`.
   */
  private applyDelta(pendingAssistantId: string, delta: ChatTurnDeltaDto): void {
    this.messages.update(items =>
      items.map(item => {
        if (item.id !== pendingAssistantId) return item;

        switch (delta.kind) {
          case ChatTurnDeltaKind.ToolCallStarted: {
            // Defensive: ignore events with no callId — the matching Completed
            // would be unbindable, leaving a hung "pending" card.
            if (!delta.toolCallId || !delta.toolName) return item;
            const events = [...(item.toolEvents ?? [])];
            events.push({
              toolCallId: delta.toolCallId,
              toolName: delta.toolName,
              description: delta.progressDescription ?? `正在执行 ${delta.toolName}…`,
              status: 'pending',
            });
            return { ...item, toolEvents: events };
          }
          case ChatTurnDeltaKind.ToolCallCompleted: {
            if (!delta.toolCallId) return item;
            const events = (item.toolEvents ?? []).map(ev =>
              ev.toolCallId === delta.toolCallId
                ? {
                    ...ev,
                    status: delta.toolCallSucceeded === false ? 'failure' as const : 'success' as const,
                    elapsedMs: delta.elapsedMs,
                  }
                : ev
            );
            return { ...item, toolEvents: events };
          }
          case ChatTurnDeltaKind.PartialText: {
            return {
              ...item,
              // First text chunk implicitly clears the spinner. If the model
              // answers without invoking any tool, this is the first thing the
              // user sees on screen.
              isPending: false,
              content: item.content + (delta.text ?? ''),
            };
          }
          case ChatTurnDeltaKind.Done: {
            return {
              ...item,
              id: delta.assistantMessageId ?? item.id,
              isPending: false,
              citations: delta.citations ?? [],
              isDegraded: delta.isDegraded,
            };
          }
          case ChatTurnDeltaKind.Error: {
            return {
              ...item,
              isPending: false,
              isError: true,
              content: delta.errorMessage ?? 'DocumentChat:SendFailed',
            };
          }
          default:
            return item;
        }
      })
    );
  }

  private toMessageView(message: ChatMessageDto): ChatMessageView {
    return {
      id: message.id ?? crypto.randomUUID(),
      role: message.role ?? ChatMessageRole.Assistant,
      content: message.content ?? '',
      creationTime: message.creationTime,
      citations: this.parseCitations(message.citationsJson),
    };
  }

  private parseCitations(json?: string | null): ChatCitationDto[] {
    if (!json) return [];
    try {
      const parsed = JSON.parse(json);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  /**
   * Wraps the first occurrence of the selected citation's snippet inside the
   * rendered Markdown container in a `<mark>` tag. Runs after every view-check
   * because the markdown HTML and the selected citation can update independently.
   *
   * Trade-offs:
   *  - First-occurrence only. If the snippet appears in multiple chunks, the UI
   *    cannot disambiguate without persisted chunk offsets (intentionally not
   *    introduced — see docs/document-chat.md citation-to-source navigation).
   *  - Snippet must match raw Markdown text from the rendered DOM. The chunker
   *    typically preserves whitespace/punctuation, so direct substring search
   *    works in practice. Falls back to `snippetMissing=true` (warning banner)
   *    when no match is found.
   */
  private tryHighlightSnippet(): void {
    const container = this.markdownContainer()?.nativeElement;
    if (!container) {
      this.lastHighlightedSnippet = null;
      this.lastHighlightedDocumentId = null;
      return;
    }

    const document = this.sourceDocument();
    const snippet = this.selectedCitation()?.citation.snippet?.trim();
    const documentId = document?.id ?? null;

    if (!snippet || !document?.markdown) {
      if (this.lastHighlightedSnippet) {
        this.removeExistingHighlight(container);
        this.lastHighlightedSnippet = null;
        this.lastHighlightedDocumentId = null;
      }
      this.snippetMissing.set(false);
      return;
    }

    if (snippet === this.lastHighlightedSnippet && documentId === this.lastHighlightedDocumentId) {
      return;
    }

    this.removeExistingHighlight(container);

    const matched = this.wrapFirstOccurrence(container, snippet);
    this.snippetMissing.set(!matched);
    this.lastHighlightedSnippet = snippet;
    this.lastHighlightedDocumentId = documentId;

    if (matched) {
      const mark = container.querySelector('mark.chat-citation-mark') as HTMLElement | null;
      mark?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
  }

  private removeExistingHighlight(container: HTMLElement): void {
    const marks = container.querySelectorAll('mark.chat-citation-mark');
    marks.forEach(mark => {
      const parent = mark.parentNode;
      if (!parent) return;
      while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
      parent.removeChild(mark);
      parent.normalize();
    });
  }

  private wrapFirstOccurrence(container: HTMLElement, snippet: string): boolean {
    const walker = (container.ownerDocument ?? document).createTreeWalker(
      container,
      NodeFilter.SHOW_TEXT
    );
    let node: Node | null;
    while ((node = walker.nextNode())) {
      const text = node.nodeValue ?? '';
      const idx = text.indexOf(snippet);
      if (idx < 0) continue;

      const range = (container.ownerDocument ?? document).createRange();
      range.setStart(node, idx);
      range.setEnd(node, idx + snippet.length);
      const mark = (container.ownerDocument ?? document).createElement('mark');
      mark.className = 'chat-citation-mark';
      mark.setAttribute('role', 'button');
      mark.setAttribute('tabindex', '0');
      mark.title = '跳到引用';
      // Bidirectional navigation: clicking the source-pane highlight scrolls the
      // matching chat-citation button into view. The citation is already selected
      // (that is what created the highlight), so we only need the scroll.
      const handler = () => this.scrollSelectedCitationIntoView();
      mark.addEventListener('click', handler);
      mark.addEventListener('keydown', (event: Event) => {
        const keyEvent = event as KeyboardEvent;
        if (keyEvent.key === 'Enter' || keyEvent.key === ' ') {
          event.preventDefault();
          handler();
        }
      });
      range.surroundContents(mark);
      return true;
    }
    return false;
  }

  private scrollSelectedCitationIntoView(): void {
    const selected = this.selectedCitation();
    if (!selected) return;
    const key = this.citationKey(selected.messageId, selected.citationIndex);
    const button = document.querySelector(
      `button.citation[data-citation-key="${CSS.escape(key)}"]`
    ) as HTMLElement | null;
    button?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }

  private loadSourceDocument(documentId: string): void {
    const requestId = ++this.sourceRequestId;
    this.isLoadingSource.set(true);
    this.sourceError.set(null);

    this.documentService
      .get(documentId)
      .pipe(finalize(() => {
        if (requestId === this.sourceRequestId) {
          this.isLoadingSource.set(false);
        }
      }))
      .subscribe({
        next: document => {
          if (requestId !== this.sourceRequestId) return;
          this.sourceDocument.set(document);
        },
        error: () => {
          if (requestId !== this.sourceRequestId) return;
          this.sourceDocument.set(null);
          this.sourceError.set('::DocumentChat:SourceLoadFailed');
        },
      });
  }
}

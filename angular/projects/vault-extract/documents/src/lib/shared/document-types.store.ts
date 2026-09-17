import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { DocumentTypeDto, DocumentTypeService } from '@dignite/ng.vault-extract';

/**
 * Where the visible document types are in their one and only fetch.
 *
 * There is no `idle`: the store issues its fetch from the constructor, so the first thing any consumer can
 * observe is `loading`. A lazy first-read trigger would have made `idle` reachable, at the price of a signal
 * read with a side effect — which is worse than a state nobody can see.
 */
export type DocumentTypesStatus = 'loading' | 'ready' | 'error';

/**
 * The visible document types of the current layer (#635 decision 7) — one fetch, one state machine, shared by
 * every page that needs them.
 *
 * Before #635 four components each called `IDocumentTypeAppService.GetVisibleAsync` for themselves and each
 * invented its own model of the answer: the upload card took "still loading" and "the fetch failed" as two
 * `input()`s from its parent, the overview and the recycle bin each kept a `typesUnavailable` signal, and the
 * list chained its review badge off the fetch's own callbacks because its gate was a function of the result.
 * Four copies of one three-state problem, each with its own bugs.
 *
 * `providedIn: 'root'` rather than per-route: the answer is the same for every page of a session, so
 * navigating list → detail → list asks once. The answer only changes when someone writes the document-type
 * layer, and in this SPA exactly one page does — `DocumentTypeListComponent`, which creates, updates,
 * archives, restores, bulk-imports types and hosts ABP's resource-permission dialog. **That page is this
 * store's invalidation site**: it calls {@link reload} after each successful write and when the permission
 * dialog closes, so a type or a grant changed in this session is picked up without every reading page
 * re-fetching on navigation. What remains stale is another session's edits, until a reload or an app
 * restart — the same window the config-state-backed module-wide permissions already have.
 *
 * `isLoading()` starts true so a consumer never reads the initial empty list as "nothing is granted to you" —
 * that specific misreading is what the upload card's deleted loading input existed to prevent.
 */
@Injectable({ providedIn: 'root' })
export class DocumentTypesStore {
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly state = signal<{
    readonly status: DocumentTypesStatus;
    readonly types: readonly DocumentTypeDto[];
  }>({ status: 'loading', types: [] });

  /**
   * The in-flight fetch, unsubscribed before a new one starts. Two overlapping `reload()`s would otherwise
   * race and let the slower answer win.
   */
  private request: Subscription | null = null;

  constructor() {
    this.fetch();
  }

  /** The visible types, empty until the first successful fetch. */
  readonly value = computed(() => this.state().types);

  /**
   * True while the answer is not in — the initial fetch and every {@link reload}. Consumers gate their
   * "nothing here" empty states on this, so an unanswered question is never rendered as a denial.
   */
  readonly isLoading = computed(() => this.state().status === 'loading');

  /**
   * True when the last fetch failed. Distinct from an empty list on purpose: a failed fetch means the
   * caller's visible types are *unknown*, and telling an operator to go ask an administrator for a grant they
   * may already hold is both wrong and unactionable.
   */
  readonly error = computed(() => this.state().status === 'error');

  /**
   * Re-asks the server. Called by the type-management page after every successful write to the layer (and
   * when the resource-permission dialog closes), and by the retry offered alongside the {@link error} state.
   */
  reload(): void {
    this.fetch();
  }

  private fetch(): void {
    this.request?.unsubscribe();
    // The previous list is kept while reloading, so a refresh does not blank a page that already has an
    // answer; only the status moves.
    this.state.update(current => ({ status: 'loading', types: current.types }));

    this.request = this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => this.state.set({ status: 'ready', types }),
        error: () => this.state.update(current => ({ status: 'error', types: current.types })),
      });
  }
}

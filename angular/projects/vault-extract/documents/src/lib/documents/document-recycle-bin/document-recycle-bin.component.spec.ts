import { TestBed } from '@angular/core/testing';
import { LIST_QUERY_DEBOUNCE_TIME, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DocumentListItemDto,
  DocumentRightsDto,
  DocumentService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { DocumentRecycleBinComponent } from './document-recycle-bin.component';

// #635 decisions 5 and 6. The recycle bin used to ask two questions of its own before it would even query
// the server: it fetched the visible document types, derived "may this caller restore anything at all" from
// their grant dictionary, and hooked the list only if the answer was yes — which is why it grew a
// hooked-yet latch and a `refresh()` that meant two different things.
//
// Both questions are gone. Admission is entry alone, the rows the server returns are the caller's whole read
// scope of deleted documents (their own included), and each row carries its own `rights`. So the page always
// queries, "no rows" can only mean the bin is empty for this caller, the latch and the two-meaning refresh
// have nothing left to cope with, and nothing on this page is derived from the type list any more — it does
// not fetch it at all.
//
// The component is constructed but never rendered where the gate under test is a computed on the instance;
// the empty-state facts render, because the message is the thing being asserted.

function row(id: string, rights: DocumentRightsDto): DocumentListItemDto {
  return { id, rights } as DocumentListItemDto;
}

const RESTORABLE = row('doc-a', { canRead: true, canRestore: true });
const READ_ONLY = row('doc-b', { canRead: true, canRestore: false });
/** A row from a server that does not send rights at all — fail closed. */
const NO_RIGHTS_ROW = { id: 'doc-c' } as DocumentListItemDto;

const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);
const WITH_PERMANENT_DELETE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.PermanentDelete,
]);

function setup(
  grantedPolicies: Set<string>,
  page: { totalCount: number; items: DocumentListItemDto[] } = { totalCount: 0, items: [] },
) {
  const getList = vi.fn().mockReturnValue(of(page));

  TestBed.configureTestingModule({
    imports: [DocumentRecycleBinComponent],
    providers: [
      // ListService debounces its query stream by 300ms by default, so without this the list request would
      // land after the assertions rather than during ngOnInit.
      { provide: LIST_QUERY_DEBOUNCE_TIME, useValue: 0 },
      {
        provide: PermissionService,
        useValue: { getGrantedPolicy: (key: string) => grantedPolicies.has(key) },
      },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn() } },
      { provide: ConfirmationService, useValue: { warn: vi.fn().mockReturnValue(of(null)) } },
      { provide: DocumentService, useValue: { getList } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentRecycleBinComponent);
  return { component: fixture.componentInstance, fixture, getList };
}

describe('DocumentRecycleBinComponent — per-row restore rights (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers Restore on exactly the rows whose rights allow it', () => {
    const { component } = setup(ENTRY_ONLY);

    expect(component.rightsOf(RESTORABLE).canRestore).toBe(true);
    expect(component.rightsOf(READ_ONLY).canRestore).toBe(false);
  });

  it('denies a row that carries no rights at all', () => {
    // A row the server did not decide is not a row the caller may act on.
    const { component } = setup(ENTRY_ONLY);

    expect(component.rightsOf(NO_RIGHTS_ROW).canRestore).toBe(false);
  });

  it('renders the actions column when any row on the page carries an action', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({ totalCount: 1, items: [READ_ONLY] });
    expect(component.hasRecycleActions()).toBe(false);

    component.documents.set({ totalCount: 2, items: [READ_ONLY, RESTORABLE] });
    expect(component.hasRecycleActions()).toBe(true);
  });

  it('keeps the actions column for a PermanentDelete holder with no restorable row', () => {
    // Permanent delete stays module-wide, by decision: no per-type grant and no ownership reaches it.
    const { component } = setup(WITH_PERMANENT_DELETE);
    component.documents.set({ totalCount: 1, items: [READ_ONLY] });

    expect(component.hasRecycleActions()).toBe(true);
  });

  it('answers per row, not per page — one page, two different verdicts', () => {
    // The rows share a page and a caller; only the server's verdict tells them apart. (Asserted through the
    // component rather than the rendered menu: abp-extensible-table needs a live datatable and the whole ABP
    // core option chain to render, which this page's specs deliberately do not stand up.)
    const { component } = setup(ENTRY_ONLY);
    component.documents.set({ totalCount: 2, items: [RESTORABLE, READ_ONLY] });

    expect(component.documents().items.map(d => component.rightsOf(d).canRestore)).toEqual([
      true,
      false,
    ]);
    expect(component.hasRecycleActions()).toBe(true);
  });
});

describe('DocumentRecycleBinComponent — the page always queries (#635 decision 6)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('hooks the list query for an entry-only caller, with no question asked first', () => {
    // Before #635 this caller was refused the page's own gate and never reached the server. The server is
    // the one that knows what they may read, so the page asks it.
    const { component, getList } = setup(ENTRY_ONLY);
    component.ngOnInit();

    expect(getList).toHaveBeenCalled();
    expect(getList.mock.calls[0][0]).toMatchObject({ isDeleted: true });
  });

  it('fetches nothing but the list — the visible types are not a dependency of this page', () => {
    // Deliberately no DocumentTypeService provider: if the component still reached for the type list, the
    // injector would fail. Nothing on this page is type-derived now that rights ride on the row.
    const { component, getList } = setup(ENTRY_ONLY);

    expect(() => component.ngOnInit()).not.toThrow();
    expect(getList).toHaveBeenCalledTimes(1);
  });

  it('refresh() means one thing: re-run the list query', () => {
    const { component, getList } = setup(ENTRY_ONLY);
    component.ngOnInit();
    expect(getList).toHaveBeenCalledTimes(1);

    component.refresh();

    expect(getList).toHaveBeenCalledTimes(2);
  });
});

describe('DocumentRecycleBinComponent — the empty state (#635 decision 6)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('reports an empty bin as empty, because the server was asked', () => {
    const { fixture, getList } = setup(ENTRY_ONLY);
    fixture.detectChanges();

    expect(getList).toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Document:RecycleBinEmpty');
  });

  it('has exactly one empty state, so no branch can claim a right the page did not check', () => {
    // The two removed states — "you may restore nothing" and "the document types could not be loaded" —
    // were both answers this page gave without having asked the server. A caller who may restore nothing
    // now simply gets no rows, which is the same thing said honestly, and the message for the first of them
    // is gone from the four localization files as well.
    const { fixture } = setup(ENTRY_ONLY);
    fixture.detectChanges();

    const emptyStates = fixture.nativeElement.querySelectorAll('.card-body.text-center');
    expect(emptyStates.length).toBe(1);
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:TypesUnavailable');
  });
});

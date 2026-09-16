import { TestBed } from '@angular/core/testing';
import { LIST_QUERY_DEBOUNCE_TIME, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Observable, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DocumentListItemDto,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { DocumentRecycleBinComponent } from './document-recycle-bin.component';

// #632 change 2, "whoever may delete may undo": the recycle bin no longer asks "does this caller hold
// Documents.Restore". Per row it asks documentRights, which ORs that module-wide permission with the DELETE
// grant on the row's own type; before it has a row it asks canRestoreAnything, the client twin of the
// server's CheckOnAnyTypeAsync gate on the list. The route is the entry permission now, so that second
// question is also what keeps a caller who may restore nothing from firing a request the server refuses.
//
// The component is constructed but never rendered: the extensible table needs a live datatable, while every
// gate under test is a computed on the instance. ngOnInit is called explicitly where the query matters.

const RESOURCES = EXTRACT_PERMISSIONS.DocumentTypes.Resources;

/** Type A — the caller holds the Delete grant, which is also its restore right. */
const TYPE_A: DocumentTypeDto = {
  id: 'type-a',
  typeCode: 'contract',
  displayName: 'Contract',
  resourcePermissions: {
    [RESOURCES.Read]: true,
    [RESOURCES.Edit]: true,
    [RESOURCES.Delete]: true,
    [RESOURCES.Upload]: true,
  },
};

/** Type B — visible but granted nothing that reaches the recycle bin. */
const TYPE_B: DocumentTypeDto = {
  id: 'type-b',
  typeCode: 'invoice',
  displayName: 'Invoice',
  resourcePermissions: {
    [RESOURCES.Read]: true,
    [RESOURCES.Edit]: false,
    [RESOURCES.Delete]: false,
    [RESOURCES.Upload]: false,
  },
};

function row(id: string, documentTypeCode: string | null): DocumentListItemDto {
  return { id, documentTypeCode } as DocumentListItemDto;
}

const A_ROW = row('doc-a', 'contract');
const B_ROW = row('doc-b', 'invoice');
const UNTYPED_ROW = row('doc-u', null);

/** Pre-#632 recycle-bin operator. */
const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.Restore,
  EXTRACT_PERMISSIONS.Documents.PermanentDelete,
]);

/** The persona #632 exists for: entry only, everything else through per-type grants. */
const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

function setup(
  grantedPolicies: Set<string>,
  types: DocumentTypeDto[] = [TYPE_A, TYPE_B],
  // A getVisible stub built per call, so a fact can make the first attempt fail and a later one succeed —
  // which is the only way to exercise the retry.
  getVisible: () => Observable<DocumentTypeDto[]> = () => of(types),
) {
  const getList = vi.fn().mockReturnValue(of({ totalCount: 0, items: [] }));

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
      { provide: DocumentTypeService, useValue: { getVisible } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentRecycleBinComponent);
  const component = fixture.componentInstance;
  return { component, fixture, getList };
}

describe('DocumentRecycleBinComponent — per-row restore rights (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers Restore on every row to a module-wide Documents.Restore holder', () => {
    const { component } = setup(MODULE_WIDE);
    component.documentTypes.set([TYPE_A, TYPE_B]);

    expect(component.rightsFor(A_ROW).canRestore).toBe(true);
    expect(component.rightsFor(B_ROW).canRestore).toBe(true);
    // Untyped rows belong to no type, so only the module-wide half ever reaches them.
    expect(component.rightsFor(UNTYPED_ROW).canRestore).toBe(true);
  });

  it('offers Restore to a Delete-grant holder on that type only', () => {
    const { component } = setup(ENTRY_ONLY);
    component.documentTypes.set([TYPE_A, TYPE_B]);

    expect(component.rightsFor(A_ROW).canRestore).toBe(true);
    expect(component.rightsFor(B_ROW).canRestore).toBe(false);
    expect(component.rightsFor(UNTYPED_ROW).canRestore).toBe(false);
  });

  it('renders the actions column when any row on the page carries an action', () => {
    const { component } = setup(ENTRY_ONLY);
    component.documentTypes.set([TYPE_A, TYPE_B]);

    component.documents.set({ totalCount: 1, items: [B_ROW] });
    expect(component.hasRecycleActions()).toBe(false);

    component.documents.set({ totalCount: 2, items: [B_ROW, A_ROW] });
    expect(component.hasRecycleActions()).toBe(true);
  });

  it('keeps the actions column for a PermanentDelete holder with no restore right at all', () => {
    // Permanent delete stays module-wide, by decision: no per-type grant reaches it.
    const { component } = setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Default, EXTRACT_PERMISSIONS.Documents.PermanentDelete]),
    );
    component.documentTypes.set([TYPE_B]);
    component.documents.set({ totalCount: 1, items: [B_ROW] });

    expect(component.canRestoreAnything()).toBe(false);
    expect(component.hasRecycleActions()).toBe(true);
  });
});

describe('DocumentRecycleBinComponent — page admission (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('queries the recycle bin for a caller holding only a Delete grant', () => {
    const { component, getList } = setup(ENTRY_ONLY);
    component.ngOnInit();

    expect(component.canRestoreAnything()).toBe(true);
    expect(getList).toHaveBeenCalled();
    expect(getList.mock.calls[0][0]).toMatchObject({ isDeleted: true });
  });

  it('queries it for a module-wide Restore holder with no grants', () => {
    const { component, getList } = setup(MODULE_WIDE, [TYPE_B]);
    component.ngOnInit();

    expect(component.canRestoreAnything()).toBe(true);
    expect(getList).toHaveBeenCalled();
  });

  it('asks the server nothing when the caller may restore nothing', () => {
    // The route is the entry permission now, so this page is reachable by every documents user. Firing the
    // list anyway would earn an AbpAuthorizationException the caller can do nothing about; the empty state
    // is the honest answer instead.
    const { component, getList } = setup(ENTRY_ONLY, [TYPE_B]);
    component.ngOnInit();

    expect(component.canRestoreAnything()).toBe(false);
    expect(getList).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
  });
});

// The empty-state template must tell these two cases apart: a caller who may restore nothing never
// queried the server at all (page-admission block above), so reporting "recycle bin is empty" would be
// false — the bin may hold rows, this caller just cannot restore any of them.
describe('DocumentRecycleBinComponent — empty-state message (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders NoRestoreRights, not RecycleBinEmpty, for a caller who may restore nothing', () => {
    const { fixture, getList } = setup(ENTRY_ONLY, [TYPE_B]);
    fixture.detectChanges();

    expect(fixture.componentInstance.canRestoreAnything()).toBe(false);
    expect(getList).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Document:RecycleBin:NoRestoreRights');
    expect(fixture.nativeElement.textContent).not.toContain('Document:RecycleBinEmpty');
  });

  it('renders RecycleBinEmpty for a caller who may restore something but gets zero rows', () => {
    const { fixture } = setup(ENTRY_ONLY);
    fixture.detectChanges();

    expect(fixture.componentInstance.canRestoreAnything()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Document:RecycleBinEmpty');
    expect(fixture.nativeElement.textContent).not.toContain('Document:RecycleBin:NoRestoreRights');
  });
});

// #632 code review: a FAILED types fetch is a third state. It used to collapse into the second one — the
// error handler emptied the type list and started listing anyway, so canRestoreAnything() said false and the
// page told a caller who does hold a Delete grant to go ask an administrator for one, on a transient network
// error. The list query was never hooked in that state either, which left Refresh inert, so nothing could
// recover it. Both halves are asserted here, because the wrong message and the dead-end are separate defects.
describe('DocumentRecycleBinComponent — the types fetch failing is its own state (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const failing = () => throwError(() => new Error('offline'));

  it('reports the fetch failure instead of claiming the caller may restore nothing', () => {
    const { fixture, getList } = setup(ENTRY_ONLY, [TYPE_A, TYPE_B], failing);
    fixture.detectChanges();

    expect(fixture.componentInstance.typesUnavailable()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Document:Upload:TypesUnavailable');
    // The two wrong answers: a denial the page cannot actually know, and a claim about the bin's contents
    // made without ever having asked the server.
    expect(fixture.nativeElement.textContent).not.toContain('Document:RecycleBin:NoRestoreRights');
    expect(fixture.nativeElement.textContent).not.toContain('Document:RecycleBinEmpty');
    expect(getList).not.toHaveBeenCalled();
    expect(fixture.componentInstance.isLoading()).toBe(false);
  });

  it('recovers through Refresh once the fetch succeeds', () => {
    // First attempt fails, the retry succeeds — so the assertion is about the retry path and not about a
    // stub that was always going to work.
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of([TYPE_A, TYPE_B]));
    const { fixture, component, getList } = setup(ENTRY_ONLY, [TYPE_A, TYPE_B], getVisible);
    fixture.detectChanges();

    expect(component.typesUnavailable()).toBe(true);
    expect(getList).not.toHaveBeenCalled();

    component.refresh();
    fixture.detectChanges();

    expect(getVisible).toHaveBeenCalledTimes(2);
    expect(component.typesUnavailable()).toBe(false);
    expect(component.canRestoreAnything()).toBe(true);
    expect(getList).toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:TypesUnavailable');
  });

  it('retries the rights fetch from the no-rights state too, rather than doing nothing', () => {
    // Same dead end, other cause: this caller really may restore nothing, so the list was never hooked and
    // Refresh had no query to re-run. Re-reading the types is what picks up a grant an administrator adds.
    const getVisible = vi.fn().mockReturnValueOnce(of([TYPE_B])).mockReturnValue(of([TYPE_A, TYPE_B]));
    const { fixture, component, getList } = setup(ENTRY_ONLY, [TYPE_B], getVisible);
    fixture.detectChanges();

    expect(component.canRestoreAnything()).toBe(false);
    expect(getList).not.toHaveBeenCalled();

    component.refresh();
    fixture.detectChanges();

    expect(component.canRestoreAnything()).toBe(true);
    expect(getList).toHaveBeenCalled();
  });
});

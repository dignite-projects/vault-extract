import { TestBed } from '@angular/core/testing';
import { LIST_QUERY_DEBOUNCE_TIME, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
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

function setup(grantedPolicies: Set<string>, types: DocumentTypeDto[] = [TYPE_A, TYPE_B]) {
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
      { provide: DocumentTypeService, useValue: { getVisible: () => of(types) } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentRecycleBinComponent);
  const component = fixture.componentInstance;
  return { component, getList };
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

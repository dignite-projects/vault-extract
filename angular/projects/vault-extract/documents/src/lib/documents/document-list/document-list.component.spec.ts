import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LIST_QUERY_DEBOUNCE_TIME, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentExportService,
  DocumentListItemDto,
  DocumentListQueryService,
  DocumentRightsDto,
  DocumentService,
  DocumentStatisticsService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
  FieldDefinitionService,
} from '@dignite/ng.vault-extract';
import { DocumentListComponent } from './document-list.component';

// #635 decision 5: per-row action gating. The list no longer re-derives what it may do with a row from the
// caller's grants and the row's type — the server decided and sent the verdict down as `rights`. Rows are
// already narrowed server-side, so nothing here hides a row; only its actions.
//
// The component is constructed but never rendered: ngOnInit fires a page's worth of requests and the
// extensible table needs a live datatable, while every gate under test is a computed on the instance.
// TestBed.tick() is what flushes the store-following effect without rendering.

const RESOURCES = EXTRACT_PERMISSIONS.DocumentTypes.Resources;

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

function row(
  id: string,
  rights: DocumentRightsDto,
  extra: Partial<DocumentListItemDto> = {},
): DocumentListItemDto {
  return { id, rights, ...extra } as DocumentListItemDto;
}

/** Everything admitted — a module-wide operator, or the owner of this row. */
const FULL: DocumentRightsDto = {
  canRead: true,
  canEdit: true,
  canReview: true,
  canDelete: true,
  canRestore: true,
  canRetry: true,
};
/** Visible, and nothing more. */
const READ_ONLY: DocumentRightsDto = {
  canRead: true,
  canEdit: false,
  canReview: false,
  canDelete: false,
  canRestore: false,
  canRetry: false,
};

const FULL_ROW = row('doc-a', FULL, { documentTypeCode: 'contract' });
const READ_ONLY_ROW = row('doc-b', READ_ONLY, { documentTypeCode: 'invoice' });
/** A row from a server that sends no rights — fail closed rather than fall through. */
const NO_RIGHTS_ROW = { id: 'doc-c' } as DocumentListItemDto;

function providers(
  grantedPolicies: Set<string>,
  statisticsSpy: ReturnType<typeof vi.fn>,
  getVisible: () => Observable<DocumentTypeDto[]>,
) {
  return [
    provideRouter([]),
    // ListService debounces its query stream by 300ms by default; ngOnInit hooks it, and the request
    // should land during the call rather than after the assertions.
    { provide: LIST_QUERY_DEBOUNCE_TIME, useValue: 0 },
    {
      provide: PermissionService,
      useValue: { getGrantedPolicy: (key: string) => grantedPolicies.has(key) },
    },
    { provide: LocalizationService, useValue: { instant: (key: string) => key } },
    { provide: ToasterService, useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn() } },
    { provide: ConfirmationService, useValue: { warn: vi.fn().mockReturnValue(of(null)) } },
    { provide: DocumentService, useValue: {} },
    { provide: DocumentListQueryService, useValue: { getList: () => of({ totalCount: 0, items: [] }) } },
    { provide: DocumentStatisticsService, useValue: { get: statisticsSpy } },
    { provide: DocumentTypeService, useValue: { getVisible } },
    { provide: FieldDefinitionService, useValue: { getFieldTypes: () => of([]) } },
    { provide: CabinetService, useValue: { getList: () => of([]) } },
    { provide: DocumentExportService, useValue: {} },
  ];
}

function setup(
  grantedPolicies: Set<string>,
  getVisible: () => Observable<DocumentTypeDto[]> = () => of([TYPE_A, TYPE_B]),
) {
  const statisticsSpy = vi.fn().mockReturnValue(of({ needsReviewCount: 3 }));

  TestBed.configureTestingModule({
    imports: [DocumentListComponent],
    providers: providers(grantedPolicies, statisticsSpy, getVisible),
  });

  const fixture = TestBed.createComponent(DocumentListComponent);
  return { component: fixture.componentInstance, fixture, statisticsSpy };
}

/** Pre-#632 operator: every module-wide permission. */
const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
  EXTRACT_PERMISSIONS.Documents.Restore,
]);

/** The persona #629/#632 exist for: entry only, everything else through per-type grants. */
const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

describe('DocumentListComponent — per-row rights come from the row (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads each row verdict exactly as the server sent it', () => {
    const { component } = setup(ENTRY_ONLY);

    expect(component.rightsOf(FULL_ROW)).toEqual(FULL);
    expect(component.rightsOf(READ_ONLY_ROW)).toEqual(READ_ONLY);
  });

  it('denies a row that carries no rights at all', () => {
    const { component } = setup(MODULE_WIDE);

    // Not "the caller holds Documents.Delete, so let it through": the row is the authority now, and a row
    // with no verdict is a row with no actions.
    expect(component.rightsOf(NO_RIGHTS_ROW)).toEqual({
      canRead: false,
      canEdit: false,
      canReview: false,
      canDelete: false,
      canRestore: false,
      canRetry: false,
    });
  });

  it('shows the actions column when at least one row carries an action', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({ totalCount: 1, items: [READ_ONLY_ROW] });
    expect(component.showActionsColumn()).toBe(false);

    component.documents.set({ totalCount: 2, items: [READ_ONLY_ROW, FULL_ROW] });
    expect(component.showActionsColumn()).toBe(true);
  });

  it('keeps the provenance actions available regardless of rights (#354)', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({
      totalCount: 1,
      items: [row('doc-x', READ_ONLY, { isContainer: true })],
    });
    expect(component.showActionsColumn()).toBe(true);
  });

  // #635: the client-side rule table keyed types by CODE against the ACTIVE visible types, so a document
  // whose type had since been archived resolved to no type at all and lost every action — while the API,
  // which keys by id across soft-deleted types, would have admitted them. With the verdict on the row there
  // is nothing left to look up, and this is the fact that says so.
  it('offers the full action set on a document whose type is not in the visible list', () => {
    const { component } = setup(ENTRY_ONLY);
    const archivedTypeRow = row('doc-archived', FULL, { documentTypeCode: 'since-archived' });

    expect(component.rightsOf(archivedTypeRow)).toEqual(FULL);
    component.documents.set({ totalCount: 1, items: [archivedTypeRow] });
    expect(component.showActionsColumn()).toBe(true);
    expect(component.canDeleteAnyRow()).toBe(true);
  });
});

describe('DocumentListComponent — bulk-delete selection (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers the checkboxes only while some row on the page is deletable', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({ totalCount: 1, items: [READ_ONLY_ROW] });
    expect(component.canDeleteAnyRow()).toBe(false);

    component.documents.set({ totalCount: 2, items: [FULL_ROW, READ_ONLY_ROW] });
    expect(component.canDeleteAnyRow()).toBe(true);
  });

  it('drops rows the caller may not delete out of the selection', () => {
    const { component } = setup(ENTRY_ONLY);

    component.onSelectionChange([FULL_ROW, READ_ONLY_ROW, NO_RIGHTS_ROW]);

    expect(component.selectedDocuments()).toEqual([FULL_ROW]);
    expect(component.selectedCount()).toBe(1);
  });
});

describe('DocumentListComponent — confirm-classification picker (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists every type for a ConfirmClassification holder', () => {
    const { component } = setup(MODULE_WIDE);

    expect(component.assignableTypes()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted target types otherwise', () => {
    const { component } = setup(ENTRY_ONLY);

    expect(component.assignableTypes()).toEqual([TYPE_A]);
  });

  it('never pre-selects a type the caller may not assign', () => {
    const { component } = setup(ENTRY_ONLY);

    component.openConfirmDialog(READ_ONLY_ROW, new Event('click'));
    expect(component.selectedTypeId()).toBe('');

    component.openConfirmDialog(FULL_ROW, new Event('click'));
    expect(component.selectedTypeId()).toBe('type-a');
  });
});

describe('DocumentListComponent — needs-review affordances (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers the review toggle to a grant-only holder with an Edit grant somewhere', () => {
    const { component } = setup(ENTRY_ONLY);

    expect(component.canReviewAnyType()).toBe(true);
  });

  it('hides the review toggle when no type carries an Edit grant', () => {
    const { component } = setup(ENTRY_ONLY, () => of([TYPE_B]));

    expect(component.canReviewAnyType()).toBe(false);
  });

  it('does not fetch the whole-layer badge count without ReadAll (it would 403)', () => {
    const { component, statisticsSpy } = setup(ENTRY_ONLY);

    expect(component.canReadAll).toBe(false);
    component['loadReviewQueueCount']();
    expect(statisticsSpy).not.toHaveBeenCalled();
  });

  it('fetches the badge count with ReadAll', () => {
    const { component, statisticsSpy } = setup(MODULE_WIDE);

    component['loadReviewQueueCount']();
    expect(statisticsSpy).toHaveBeenCalled();
    expect(component.reviewQueueCount()).toBe(3);
  });
});

// The badge's gate is a function of the visible types, so a fetch issued before they land answers "no" and,
// with nothing asking again, leaves the badge at a permanent 0 for exactly the persona per-type grants exist
// for. #632 solved that by hanging the fetch off this page's own getVisible callbacks; #635 moved the fetch
// into a shared store, so it follows the store's state instead. These facts drive that path.
describe('DocumentListComponent — the badge follows the store (#635 decision 7)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  /** ReadAll, but the only EDIT right is a per-type grant — the caller the ordering bug silenced. */
  const READ_ALL_GRANTS_ONLY = new Set<string>([
    EXTRACT_PERMISSIONS.Documents.Default,
    EXTRACT_PERMISSIONS.Documents.ReadAll,
  ]);

  it('waits for the types, then fetches it for a caller whose only edit right is a per-type grant', () => {
    const pending = new Subject<DocumentTypeDto[]>();
    const { component, fixture, statisticsSpy } = setup(READ_ALL_GRANTS_ONLY, () =>
      pending.asObservable(),
    );
    component.ngOnInit();
    TestBed.tick();

    // Still loading: the gate cannot answer anything but "module-wide only", so nothing is asked yet.
    expect(component.canReviewAnyType()).toBe(false);
    expect(statisticsSpy).not.toHaveBeenCalled();

    pending.next([TYPE_A, TYPE_B]);
    TestBed.tick();

    expect(component.canReviewAnyType()).toBe(true);
    expect(statisticsSpy).toHaveBeenCalled();
    expect(component.reviewQueueCount()).toBe(3);
    expect(fixture).toBeTruthy();
  });

  it('still fetches it for a module-wide holder, whose gate never needed the types', () => {
    const { component, statisticsSpy } = setup(MODULE_WIDE);

    component.ngOnInit();
    TestBed.tick();

    expect(statisticsSpy).toHaveBeenCalled();
    expect(component.reviewQueueCount()).toBe(3);
  });

  it('skips it for a caller with no edit right on any visible type', () => {
    const { component, statisticsSpy } = setup(READ_ALL_GRANTS_ONLY, () => of([TYPE_B]));

    component.ngOnInit();
    TestBed.tick();

    expect(component.canReviewAnyType()).toBe(false);
    expect(statisticsSpy).not.toHaveBeenCalled();
  });

  it('falls back to the module-wide answer when the types fetch fails', () => {
    // The failure branch has to reach the fetch too: an empty type list reduces the gate to its module-wide
    // half, which is a real answer, and a module-wide reviewer must still get their badge.
    const { component, statisticsSpy } = setup(MODULE_WIDE, () =>
      throwError(() => new Error('offline')),
    );

    component.ngOnInit();
    TestBed.tick();

    expect(component.documentTypes.error()).toBe(true);
    expect(component.documentTypes.value()).toEqual([]);
    expect(statisticsSpy).toHaveBeenCalled();
  });
});

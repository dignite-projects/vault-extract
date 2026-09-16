import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentExportService,
  DocumentListItemDto,
  DocumentListQueryService,
  DocumentService,
  DocumentStatisticsService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
  FieldDefinitionService,
} from '@dignite/ng.vault-extract';
import { DocumentListComponent } from './document-list.component';

// #632: per-row action gating. The list no longer asks "does this caller hold Documents.Delete /
// ConfirmClassification"; it asks documentRights, which ORs that module-wide permission with the matching
// grant on the ROW's own document type. Rows are already narrowed server-side, so nothing here hides a row —
// only its actions.
//
// The component is constructed but never rendered: ngOnInit fires a page's worth of requests and the
// extensible table needs a live datatable, while every gate under test is a computed on the instance.

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

function row(id: string, documentTypeCode: string | null): DocumentListItemDto {
  return { id, documentTypeCode } as DocumentListItemDto;
}

const A_ROW = row('doc-a', 'contract');
const B_ROW = row('doc-b', 'invoice');
const UNTYPED_ROW = row('doc-u', null);

function setup(grantedPolicies: Set<string>) {
  const statisticsSpy = vi.fn().mockReturnValue(of({ needsReviewCount: 3 }));

  TestBed.configureTestingModule({
    imports: [DocumentListComponent],
    providers: [
      provideRouter([]),
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
      { provide: DocumentTypeService, useValue: { getVisible: () => of([TYPE_A, TYPE_B]) } },
      { provide: FieldDefinitionService, useValue: { getFieldTypes: () => of([]) } },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
      { provide: DocumentExportService, useValue: {} },
    ],
  });

  const fixture = TestBed.createComponent(DocumentListComponent);
  const component = fixture.componentInstance;
  component.documentTypes.set([TYPE_A, TYPE_B]);
  return { component, statisticsSpy };
}

/** Pre-#632 operator: every module-wide permission. */
const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
  EXTRACT_PERMISSIONS.Documents.Restore,
]);

/** The persona #632 exists for: entry only, everything else through per-type grants. */
const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

describe('DocumentListComponent — per-row rights (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('gives a module-wide holder every action on every row, typed or not', () => {
    const { component } = setup(MODULE_WIDE);

    expect(component.rightsFor(A_ROW)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
    expect(component.rightsFor(B_ROW)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
    expect(component.rightsFor(UNTYPED_ROW)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
  });

  it('gives a grant-only holder actions on the granted type only', () => {
    const { component } = setup(ENTRY_ONLY);

    // canRestore rides on the same Delete grant (#632 change 2): whoever may delete may undo.
    expect(component.rightsFor(A_ROW)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
    // Type B carries Read but neither Edit nor Delete — the row is visible, its actions are not.
    expect(component.rightsFor(B_ROW)).toEqual({
      canRead: true,
      canEdit: false,
      canDelete: false,
      canRestore: false,
    });
  });

  it('gives a grant-only holder nothing on an untyped row', () => {
    const { component } = setup(ENTRY_ONLY);

    expect(component.rightsFor(UNTYPED_ROW)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
      canRestore: false,
    });
  });

  it('shows the actions column when at least one row carries an action', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({ totalCount: 1, items: [B_ROW] });
    expect(component.showActionsColumn()).toBe(false);

    component.documents.set({ totalCount: 2, items: [B_ROW, A_ROW] });
    expect(component.showActionsColumn()).toBe(true);
  });

  it('keeps the provenance actions available regardless of rights (#354)', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({
      totalCount: 1,
      items: [{ ...B_ROW, isContainer: true } as DocumentListItemDto],
    });
    expect(component.showActionsColumn()).toBe(true);
  });
});

describe('DocumentListComponent — bulk-delete selection (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers the checkboxes only while some row on the page is deletable', () => {
    const { component } = setup(ENTRY_ONLY);

    component.documents.set({ totalCount: 1, items: [B_ROW] });
    expect(component.canDeleteAnyRow()).toBe(false);

    component.documents.set({ totalCount: 2, items: [A_ROW, B_ROW] });
    expect(component.canDeleteAnyRow()).toBe(true);
  });

  it('drops rows the caller may not delete out of the selection', () => {
    const { component } = setup(ENTRY_ONLY);

    component.onSelectionChange([A_ROW, B_ROW, UNTYPED_ROW]);

    expect(component.selectedDocuments()).toEqual([A_ROW]);
    expect(component.selectedCount()).toBe(1);
  });

  it('keeps every selected row for a module-wide holder', () => {
    const { component } = setup(MODULE_WIDE);

    component.onSelectionChange([A_ROW, B_ROW, UNTYPED_ROW]);

    expect(component.selectedDocuments()).toEqual([A_ROW, B_ROW, UNTYPED_ROW]);
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

    component.openConfirmDialog(B_ROW, new Event('click'));
    expect(component.selectedTypeId()).toBe('');

    component.openConfirmDialog(A_ROW, new Event('click'));
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
    const { component } = setup(ENTRY_ONLY);

    component.documentTypes.set([TYPE_B]);
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

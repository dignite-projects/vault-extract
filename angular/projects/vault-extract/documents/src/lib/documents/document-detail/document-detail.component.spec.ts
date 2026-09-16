import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentDto,
  DocumentPipelineRunService,
  DocumentReviewReasons,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
  FieldDefinitionService,
} from '@dignite/ng.vault-extract';
import { DocumentDetailComponent } from './document-detail.component';

// #632: the detail page's whole edit family (confirm / reclassify / re-recognize / re-extract / field editing
// / Markdown correction / reject / allow duplicate / resolve warnings) already derived from one gate,
// canEditFields. Making that gate per-document carries the family over at once — which is exactly what the
// backend does, where all nine endpoints share DocumentAccessRule.Edit.
//
// The component is constructed but never rendered: ngOnInit resolves a route id and fires the document load,
// while every gate under test is a computed on the instance.

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

/** A loaded, already-extracted document — the state in which the edit affordances are offered. */
function documentOfType(documentTypeCode: string | null): DocumentDto {
  return {
    id: 'doc-1',
    documentTypeCode,
    markdown: '# body',
    reviewReasons: DocumentReviewReasons.UnresolvedClassification,
  } as DocumentDto;
}

function setup(grantedPolicies: Set<string>) {
  TestBed.configureTestingModule({
    imports: [DocumentDetailComponent],
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
      { provide: DocumentPipelineRunService, useValue: { getList: () => of([]) } },
      { provide: DocumentTypeService, useValue: { getVisible: () => of([TYPE_A, TYPE_B]) } },
      { provide: FieldDefinitionService, useValue: { getFieldTypes: () => of([]) } },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
    ],
  });

  const component = TestBed.createComponent(DocumentDetailComponent).componentInstance;
  component.documentTypes.set([TYPE_A, TYPE_B]);
  return component;
}

const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
]);

const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

describe('DocumentDetailComponent — per-document rights (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('gives a module-wide holder edit and delete on any type, and on an untyped document', () => {
    const component = setup(MODULE_WIDE);

    component.document.set(documentOfType('invoice'));
    expect(component.canEditFields()).toBe(true);
    expect(component.canDelete()).toBe(true);

    component.document.set(documentOfType(null));
    expect(component.canEditFields()).toBe(true);
    expect(component.canDelete()).toBe(true);
  });

  it('gives a grant-only holder edit and delete on the granted type only', () => {
    const component = setup(ENTRY_ONLY);

    component.document.set(documentOfType('contract'));
    expect(component.canEditFields()).toBe(true);
    expect(component.canDelete()).toBe(true);

    component.document.set(documentOfType('invoice'));
    expect(component.canEditFields()).toBe(false);
    expect(component.canDelete()).toBe(false);
  });

  it('denies a grant-only holder on an untyped document — no grant can name it', () => {
    const component = setup(ENTRY_ONLY);

    component.document.set(documentOfType(null));
    expect(component.canEditFields()).toBe(false);
    expect(component.canDelete()).toBe(false);
  });

  it('denies everything before a document is loaded', () => {
    const component = setup(MODULE_WIDE);

    expect(component.canEditFields()).toBe(false);
    expect(component.canDelete()).toBe(false);
  });

  it('carries the whole edit family with it', () => {
    const component = setup(ENTRY_ONLY);
    component.isLoading.set(false);

    component.document.set(documentOfType('contract'));
    expect(component.needsClassification()).toBe(true);
    expect(component.canRerecognize()).toBe(true);
    expect(component.canReextractFields()).toBe(true);
    expect(component.canEditMarkdown()).toBe(true);

    component.document.set(documentOfType('invoice'));
    expect(component.needsClassification()).toBe(false);
    expect(component.canRerecognize()).toBe(false);
    expect(component.canReextractFields()).toBe(false);
    expect(component.canEditMarkdown()).toBe(false);
  });
});

describe('DocumentDetailComponent — classify picker (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists every type for a ConfirmClassification holder', () => {
    expect(setup(MODULE_WIDE).assignableTypes()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted target types otherwise', () => {
    expect(setup(ENTRY_ONLY).assignableTypes()).toEqual([TYPE_A]);
  });

  it('never pre-selects a type the caller may not assign', () => {
    const component = setup(ENTRY_ONLY);

    component.document.set(documentOfType('invoice'));
    component.openClassifyDialog();
    expect(component.selectedTypeId()).toBe('');

    component.closeClassifyDialog();
    component.document.set(documentOfType('contract'));
    component.openClassifyDialog();
    expect(component.selectedTypeId()).toBe('type-a');
  });
});

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CabinetService, DocumentUploadService, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import { DocumentUploadComponent } from './document-upload.component';

// #623: the "declared document type" selector is an operator-confirmation shortcut that
// skips LLM classification on the backend (ConfirmClassification semantics), so it must
// only render for callers holding that permission, and the selection must flow through to
// every file in a batch upload.
//
// Code review (2026-09-05): the type list is no longer fetched by this component — it is
// supplied by the parent (DocumentOverviewComponent, its only mount site) via the
// `documentTypes` input, so these specs set the input directly instead of stubbing
// DocumentTypeService.

const DOCUMENT_TYPES = [
  { id: 'type-1', displayName: 'Contract' },
  { id: 'type-2', displayName: 'Invoice' },
];

function fakeFile(name = 'contract.pdf'): File {
  return new File(['content'], name, { type: 'application/pdf' });
}

async function setup(
  grantedPolicies: Set<string>,
  documentTypes = DOCUMENT_TYPES,
  options: { documentTypesLoading?: boolean; documentTypesUnavailable?: boolean } = {},
) {
  const uploadSpy = vi.fn().mockReturnValue(of({ id: 'doc-1' }));
  const toasterSpy = { success: vi.fn(), error: vi.fn(), warn: vi.fn() };

  await TestBed.configureTestingModule({
    imports: [DocumentUploadComponent],
    providers: [
      provideRouter([]),
      {
        provide: PermissionService,
        useValue: { getGrantedPolicy: (key: string) => grantedPolicies.has(key) },
      },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: toasterSpy },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
      { provide: DocumentUploadService, useValue: { upload: uploadSpy } },
    ],
  }).compileComponents();

  const fixture: ComponentFixture<DocumentUploadComponent> = TestBed.createComponent(
    DocumentUploadComponent,
  );
  fixture.componentRef.setInput('documentTypes', documentTypes);
  if (options.documentTypesLoading !== undefined) {
    fixture.componentRef.setInput('documentTypesLoading', options.documentTypesLoading);
  }
  if (options.documentTypesUnavailable !== undefined) {
    fixture.componentRef.setInput('documentTypesUnavailable', options.documentTypesUnavailable);
  }
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();

  return { fixture, uploadSpy, toasterSpy };
}

describe('DocumentUploadComponent — declared document type (#623)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('hides the selector without ConfirmClassification (Upload only)', async () => {
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]));
    const component = fixture.componentInstance;

    expect(component.canDeclareType).toBe(false);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
  });

  it('hides the selector with Upload + Documents.Default but without ConfirmClassification', async () => {
    // Proves the ConfirmClassification conjunct is required on its own — holding the list-read
    // permission (Documents.Default) is not a substitute for the confirmation permission.
    const { fixture } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.Default]),
    );
    const component = fixture.componentInstance;

    expect(component.canDeclareType).toBe(false);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
  });

  it('shows the selector with Upload + ConfirmClassification, rendering options from the input', async () => {
    const { fixture } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
    );
    const component = fixture.componentInstance;

    expect(component.canDeclareType).toBe(true);
    const select = fixture.nativeElement.querySelector('.document-type-select');
    expect(select).not.toBeNull();
    const options = select.querySelectorAll('option');
    // "Let AI classify" placeholder + one option per declared type.
    expect(options.length).toBe(DOCUMENT_TYPES.length + 1);
  });

  it('hides the selector when granted but the input list is empty', async () => {
    const { fixture } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
      [],
    );
    const component = fixture.componentInstance;

    expect(component.canDeclareType).toBe(true);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
  });

  it('passes the selected DocumentTypeId through to every file in a batch upload', async () => {
    const { fixture, uploadSpy } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
    );
    const component = fixture.componentInstance;
    component.selectedDocumentTypeId.set('type-1');

    (component as any).uploadFiles([fakeFile('a.pdf'), fakeFile('b.pdf')]);

    expect(uploadSpy).toHaveBeenCalledTimes(2);
    expect(uploadSpy).toHaveBeenNthCalledWith(1, expect.any(File), undefined, 'type-1');
    expect(uploadSpy).toHaveBeenNthCalledWith(2, expect.any(File), undefined, 'type-1');
  });

  it('does not send a DocumentTypeId when no type is declared', async () => {
    const { fixture, uploadSpy } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
    );
    const component = fixture.componentInstance;

    (component as any).uploadFiles([fakeFile('a.pdf')]);

    expect(uploadSpy).toHaveBeenCalledWith(expect.any(File), undefined, undefined);
  });
});

// #629: an Upload-only caller (no ConfirmClassification) no longer gets an untyped fallback — the
// backend now requires ConfirmClassification for untyped upload too, to keep the per-type ACL from
// being bypassed by letting the LLM pick the type. Such a caller's declarable scope narrows to the
// types DocumentTypeDto.resourcePermissions reports its own Upload grant on.
const UPLOAD_RESOURCE_KEY = EXTRACT_PERMISSIONS.DocumentTypes.Resources.Upload;

describe('DocumentUploadComponent — per-type upload grant (#629)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers only the granted type, hides LetAiClassify, and pre-selects it when exactly one type is granted', async () => {
    const types = [
      { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } },
      { id: 'type-2', displayName: 'Invoice', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: false } },
    ];
    const { fixture, uploadSpy } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), types);
    const component = fixture.componentInstance;

    expect(component.canDeclareType).toBe(false);
    expect(component.declarableTypes()).toEqual([types[0]]);

    const select = fixture.nativeElement.querySelector('.document-type-select');
    expect(select).not.toBeNull();
    const options: HTMLOptionElement[] = Array.from(select.querySelectorAll('option'));
    // Disabled placeholder first (required mode), then the single granted type — no LetAiClassify.
    expect(options.length).toBe(2);
    expect(options[0].value).toBe('');
    expect(options[0].disabled).toBe(true);
    expect(options[1].value).toBe('type-1');

    // Pre-selected: exactly one declarable type, nothing to actually pick. The placeholder is never
    // the visible choice here — selectedDocumentTypeId already points at the real option.
    expect(component.selectedDocumentTypeId()).toBe('type-1');

    (component as any).uploadFiles([fakeFile('a.pdf')]);
    expect(uploadSpy).toHaveBeenCalledWith(expect.any(File), undefined, 'type-1');
  });

  it('offers only the granted types among several and does not pre-select when more than one is granted', async () => {
    const types = [
      { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } },
      { id: 'type-2', displayName: 'Invoice', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } },
      { id: 'type-3', displayName: 'Resume', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: false } },
    ];
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), types);
    const component = fixture.componentInstance;

    expect(component.declarableTypes()).toEqual([types[0], types[1]]);

    const select = fixture.nativeElement.querySelector('.document-type-select');
    expect(select).not.toBeNull();
    const options: HTMLOptionElement[] = Array.from(select.querySelectorAll('option'));
    // No LetAiClassify — instead a disabled placeholder leads, since nothing is pre-selected when
    // more than one type is granted; the two granted types follow it.
    expect(options[0].value).toBe('');
    expect(options[0].disabled).toBe(true);
    expect(options.map(o => o.value)).toEqual(['', 'type-1', 'type-2']);
    expect(component.selectedDocumentTypeId()).toBe('');
  });

  it('renders the empty state and keeps the file input out of reach when no type is granted', async () => {
    const types = [
      { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: false } },
    ];
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), types);
    const component = fixture.componentInstance;

    expect(component.declarableTypes()).toEqual([]);
    expect(component.hasNoGrantableTypes()).toBe(true);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
    expect(fixture.nativeElement.querySelector('input[type="file"]')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Document:Upload:NoGrantableTypes');
  });

  it('a ConfirmClassification caller still sees every type plus LetAiClassify (unchanged #623 behaviour)', async () => {
    const { fixture } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload, EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
      DOCUMENT_TYPES,
    );
    const component = fixture.componentInstance;

    expect(component.declarableTypes()).toEqual(DOCUMENT_TYPES);
    const select = fixture.nativeElement.querySelector('.document-type-select');
    const options = select.querySelectorAll('option');
    expect(options.length).toBe(DOCUMENT_TYPES.length + 1);
  });
});

// Code review (2026-09-15): the empty state must not flash or mask a load error, a drop before a
// required type is chosen must be refused visibly rather than silently dropped, and a stale selection
// (the declarable set changed out from under it) must not stay valid.
describe('DocumentUploadComponent — loading / unavailable states and refused drop (#629 code review)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('while types are still loading, shows neither empty state, disables the buttons, and offers no selector', async () => {
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), [], {
      documentTypesLoading: true,
    });
    const component = fixture.componentInstance;

    expect(component.hasNoGrantableTypes()).toBe(false);
    expect(component.showTypesUnavailable()).toBe(false);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:NoGrantableTypes');

    const browseButton = fixture.nativeElement.querySelector('button.btn-primary');
    expect(browseButton).not.toBeNull();
    expect(browseButton.disabled).toBe(true);
  });

  it('shows the TypesUnavailable state (not NoGrantableTypes) when the types fetch failed', async () => {
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), [], {
      documentTypesUnavailable: true,
    });
    const component = fixture.componentInstance;

    expect(component.showTypesUnavailable()).toBe(true);
    expect(component.hasNoGrantableTypes()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Document:Upload:TypesUnavailable');
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:NoGrantableTypes');
  });

  it('refuses a drop before a required type is selected with a warning toast, not a silent no-op', async () => {
    const types = [
      { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } },
      { id: 'type-2', displayName: 'Invoice', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } },
    ];
    const { fixture, uploadSpy, toasterSpy } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload]),
      types,
    );
    const component = fixture.componentInstance;
    // Two granted types: the pre-selection effect only fires for exactly one, so nothing is chosen yet.
    expect(component.selectedDocumentTypeId()).toBe('');

    const dropEvent = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
      dataTransfer: { files: [fakeFile('a.pdf')] },
    } as unknown as DragEvent;
    component.onDrop(dropEvent);

    expect(uploadSpy).not.toHaveBeenCalled();
    expect(toasterSpy.warn).toHaveBeenCalledTimes(1);
    expect(toasterSpy.warn).toHaveBeenCalledWith('::Document:SelectDocumentType:Required');
  });

  it('invalidates a stale selection when the declarable set changes out from under it', async () => {
    const typeA = { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } };
    const typeB = { id: 'type-2', displayName: 'Invoice', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } };
    const { fixture } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), [typeA]);
    const component = fixture.componentInstance;

    // Single declarable type: the pre-selection effect fires.
    expect(component.selectedDocumentTypeId()).toBe('type-1');
    expect(component.typeSelectionSatisfied()).toBe(true);

    fixture.componentRef.setInput('documentTypes', [typeB]);
    fixture.detectChanges();

    expect(component.selectedDocumentTypeId()).toBe('type-1');
    expect(component.typeSelectionSatisfied()).toBe(false);
  });
});

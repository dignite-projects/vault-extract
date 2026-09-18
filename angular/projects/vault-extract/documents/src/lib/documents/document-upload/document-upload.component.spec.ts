import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentTypeDto,
  DocumentTypeService,
  DocumentUploadService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { DocumentTypesStore } from '../../shared/document-types.store';
import { DocumentUploadComponent } from './document-upload.component';

// #623: the "declared document type" selector is an operator-confirmation shortcut that
// skips LLM classification on the backend (ConfirmClassification semantics), so it must
// only render for callers holding that permission, and the selection must flow through to
// every file in a batch upload.
//
// #635 decision 7: the type list is neither fetched here nor handed down by the parent any more — it comes
// from the shared DocumentTypesStore, which also answers "still loading" and "the fetch failed" for itself.
// These specs drive the real store by stubbing the one call behind it.

const DOCUMENT_TYPES: DocumentTypeDto[] = [
  { id: 'type-1', displayName: 'Contract' },
  { id: 'type-2', displayName: 'Invoice' },
];

function fakeFile(name = 'contract.pdf'): File {
  return new File(['content'], name, { type: 'application/pdf' });
}

async function setup(
  grantedPolicies: Set<string>,
  documentTypes: DocumentTypeDto[] = DOCUMENT_TYPES,
  // Overriding this is how a fact puts the store in a state other than "resolved": a Subject holds it
  // loading, throwError puts it in its failure state.
  getVisible: () => Observable<DocumentTypeDto[]> = () => of(documentTypes),
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
      { provide: DocumentTypeService, useValue: { getVisible } },
    ],
  }).compileComponents();

  const fixture: ComponentFixture<DocumentUploadComponent> = TestBed.createComponent(
    DocumentUploadComponent,
  );
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();

  return { fixture, uploadSpy, toasterSpy, store: TestBed.inject(DocumentTypesStore) };
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
    expect(component.assignableTypes()).toEqual([types[0]]);

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

    expect(component.assignableTypes()).toEqual([types[0], types[1]]);

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

    expect(component.assignableTypes()).toEqual([]);
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

    expect(component.assignableTypes()).toEqual(DOCUMENT_TYPES);
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
    // The store holds `loading` for as long as the underlying call has not answered. Reading its empty list
    // as "nothing is granted to you" is the exact misinformation this state exists to prevent.
    const pending = new Subject<DocumentTypeDto[]>();
    const { fixture, store } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload]),
      [],
      () => pending.asObservable(),
    );
    const component = fixture.componentInstance;

    expect(store.isLoading()).toBe(true);

    expect(component.hasNoGrantableTypes()).toBe(false);
    expect(component.showTypesUnavailable()).toBe(false);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:NoGrantableTypes');

    const browseButton = fixture.nativeElement.querySelector('button.btn-primary');
    expect(browseButton).not.toBeNull();
    expect(browseButton.disabled).toBe(true);
  });

  it('shows the TypesUnavailable state (not NoGrantableTypes) when the types fetch failed', async () => {
    const { fixture, store } = await setup(new Set([EXTRACT_PERMISSIONS.Documents.Upload]), [], () =>
      throwError(() => new Error('offline')),
    );
    const component = fixture.componentInstance;

    expect(store.error()).toBe(true);
    expect(component.showTypesUnavailable()).toBe(true);
    expect(component.hasNoGrantableTypes()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Document:Upload:TypesUnavailable');
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:NoGrantableTypes');
  });

  it('recovers from the failed fetch through the retry, for this page and every other', async () => {
    // #635: the retry reloads the STORE, so the types the recycle bin, the list and the detail page all
    // read recover with it — there is one fetch left to recover.
    // Fails twice: once for the store's own first fetch, once for the retryIfFailed() this card makes on
    // arrival (the synchronous stub has already failed by ngOnInit; over real HTTP the first fetch would
    // still be in flight and that retry would be a no-op). Only then is the button the way out.
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of([{ id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } }]));
    const { fixture, store } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload]),
      [],
      getVisible,
    );
    const component = fixture.componentInstance;

    expect(component.showTypesUnavailable()).toBe(true);

    store.reload();
    fixture.detectChanges();

    expect(getVisible).toHaveBeenCalledTimes(3);
    expect(component.showTypesUnavailable()).toBe(false);
    expect(component.assignableTypes().length).toBe(1);
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:TypesUnavailable');
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
    const getVisible = vi.fn().mockReturnValueOnce(of([typeA])).mockReturnValue(of([typeB]));
    const { fixture, store } = await setup(
      new Set([EXTRACT_PERMISSIONS.Documents.Upload]),
      [typeA],
      getVisible,
    );
    const component = fixture.componentInstance;

    // Single declarable type: the pre-selection effect fires.
    expect(component.selectedDocumentTypeId()).toBe('type-1');
    expect(component.typeSelectionSatisfied()).toBe(true);

    // A grant revoked between one reload and the next: the selection still names type-1, which is no
    // longer declarable, so it must stop counting as a satisfied choice.
    store.reload();
    fixture.detectChanges();

    expect(component.selectedDocumentTypeId()).toBe('type-1');
    expect(component.typeSelectionSatisfied()).toBe(false);
  });
});

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

// #645: `Documents.Upload` means "upload into every type" (untyped included); a type-level Upload grant
// means "upload into this type" and suffices on its own; ConfirmClassification is not part of uploading.
//
// #635 decision 7: the type list comes from the shared DocumentTypesStore, which also answers "still loading"
// and "the fetch failed" for itself. These specs drive the real store by stubbing the one call behind it.

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

const UPLOAD_RESOURCE_KEY = EXTRACT_PERMISSIONS.DocumentTypes.Resources.Upload;

/** `Documents.Upload` ("上传到所有文档类型"): upload into every type, and untyped. */
const UPLOAD_INTO_ALL = new Set([EXTRACT_PERMISSIONS.Documents.Default, EXTRACT_PERMISSIONS.Documents.Upload]);
/** #645's ordinary uploader: entry, plus type-level Upload grants carried on the types themselves. */
const GRANT_ONLY = new Set([EXTRACT_PERMISSIONS.Documents.Default]);
/** A reviewer who may edit every type but holds no upload right of any kind. */
const CONFIRM_ONLY = new Set([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
]);

const GRANTED_A = { id: 'type-1', displayName: 'Contract', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } };
const GRANTED_B = { id: 'type-2', displayName: 'Invoice', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: true } };
const NOT_GRANTED = { id: 'type-3', displayName: 'Resume', resourcePermissions: { [UPLOAD_RESOURCE_KEY]: false } };

function optionValues(fixture: ComponentFixture<DocumentUploadComponent>): string[] {
  const select = fixture.nativeElement.querySelector('.document-type-select select');
  if (!select) return [];
  return Array.from(select.querySelectorAll('option') as NodeListOf<HTMLOptionElement>).map(o => o.value);
}

function hasUntypedOption(fixture: ComponentFixture<DocumentUploadComponent>): boolean {
  return (fixture.nativeElement.textContent as string).includes('Document:LetAiClassify');
}

// #645 decision 4: the untyped option ("交由 AI 分类") exists exactly for `Documents.Upload`. A caller whose
// upload right is per-type must name the type — the classifier may land an untyped document in any type,
// which is #629's reason and is unchanged. ConfirmClassification no longer plays any part here.
describe('DocumentUploadComponent — the untyped option (#645)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('is offered with Documents.Upload, and an untyped upload sends no DocumentTypeId', async () => {
    const { fixture, uploadSpy } = await setup(UPLOAD_INTO_ALL, [GRANTED_A, NOT_GRANTED]);
    const component = fixture.componentInstance;

    expect(component.canUploadIntoAllTypes).toBe(true);
    expect(component.requiresTypeSelection).toBe(false);
    expect(hasUntypedOption(fixture)).toBe(true);

    (component as any).uploadFiles([fakeFile('a.pdf')]);
    expect(uploadSpy).toHaveBeenCalledWith(expect.any(File), undefined, undefined);
  });

  it('is not offered to a grant-only uploader', async () => {
    const { fixture } = await setup(GRANT_ONLY, [GRANTED_A, GRANTED_B]);

    expect(fixture.componentInstance.requiresTypeSelection).toBe(true);
    expect(hasUntypedOption(fixture)).toBe(false);
  });

  it('is not offered for ConfirmClassification, which left the upload path', async () => {
    const { fixture } = await setup(CONFIRM_ONLY, [GRANTED_A]);

    expect(fixture.componentInstance.canUploadIntoAllTypes).toBe(false);
    expect(hasUntypedOption(fixture)).toBe(false);
  });
});

// #645 decision 4: the picker is `assignableDocumentTypes(types, Documents.Upload)` — every type with the
// role-level permission, otherwise exactly the types carrying the caller's own Upload grant.
describe('DocumentUploadComponent — the type picker (#645)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists every type, after the untyped option, for Documents.Upload', async () => {
    const { fixture } = await setup(UPLOAD_INTO_ALL, [GRANTED_A, NOT_GRANTED]);

    expect(fixture.componentInstance.assignableTypes()).toEqual([GRANTED_A, NOT_GRANTED]);
    // The leading '' is the untyped option here, not a placeholder.
    expect(optionValues(fixture)).toEqual(['', 'type-1', 'type-3']);
  });

  it('lists only the granted types for a grant-only uploader, behind a disabled placeholder', async () => {
    const { fixture } = await setup(GRANT_ONLY, [GRANTED_A, GRANTED_B, NOT_GRANTED]);
    const component = fixture.componentInstance;

    expect(component.assignableTypes()).toEqual([GRANTED_A, GRANTED_B]);
    expect(optionValues(fixture)).toEqual(['', 'type-1', 'type-2']);
    const placeholder = fixture.nativeElement.querySelector('.document-type-select option') as HTMLOptionElement;
    expect(placeholder.disabled).toBe(true);
    // Two choices: nothing is pre-selected.
    expect(component.selectedDocumentTypeId()).toBe('');
  });

  it('lists only the Upload-granted types for ConfirmClassification — editing every type is not uploading into it', async () => {
    const { fixture } = await setup(CONFIRM_ONLY, [GRANTED_A, NOT_GRANTED]);

    expect(fixture.componentInstance.assignableTypes()).toEqual([GRANTED_A]);
    expect(optionValues(fixture)).toEqual(['', 'type-1']);
  });

  it('pre-selects the only granted type, and uploads into it', async () => {
    const { fixture, uploadSpy } = await setup(GRANT_ONLY, [GRANTED_A, NOT_GRANTED]);
    const component = fixture.componentInstance;

    // Exactly one declarable type: nothing to actually pick.
    expect(component.selectedDocumentTypeId()).toBe('type-1');

    (component as any).uploadFiles([fakeFile('a.pdf')]);
    expect(uploadSpy).toHaveBeenCalledWith(expect.any(File), undefined, 'type-1');
  });

  it('passes the selected DocumentTypeId through to every file in a batch upload', async () => {
    const { fixture, uploadSpy } = await setup(UPLOAD_INTO_ALL, [GRANTED_A, NOT_GRANTED]);
    const component = fixture.componentInstance;
    component.selectedDocumentTypeId.set('type-3');

    (component as any).uploadFiles([fakeFile('a.pdf'), fakeFile('b.pdf')]);

    expect(uploadSpy).toHaveBeenCalledTimes(2);
    expect(uploadSpy).toHaveBeenNthCalledWith(1, expect.any(File), undefined, 'type-3');
    expect(uploadSpy).toHaveBeenNthCalledWith(2, expect.any(File), undefined, 'type-3');
  });

  it('hides the selector for Documents.Upload when the layer has no types, and still uploads untyped', async () => {
    const { fixture, uploadSpy } = await setup(UPLOAD_INTO_ALL, []);

    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();
    (fixture.componentInstance as any).uploadFiles([fakeFile('a.pdf')]);
    expect(uploadSpy).toHaveBeenCalledWith(expect.any(File), undefined, undefined);
  });
});

// The card's remaining states. #645 removed one: "you have been granted no type" can no longer render,
// because the overview shows its read-only card instead once the type store has answered with no grant —
// see DocumentOverviewComponent.uploadSlot. What stays is loading (a reload in flight) and the failed fetch,
// which is the grant-only uploader's only way back.
describe('DocumentUploadComponent — loading / unavailable states and refused drop', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('while types are still loading, disables the buttons and offers no selector', async () => {
    const pending = new Subject<DocumentTypeDto[]>();
    const { fixture, store } = await setup(GRANT_ONLY, [], () => pending.asObservable());
    const component = fixture.componentInstance;

    expect(store.isLoading()).toBe(true);
    expect(component.showTypesUnavailable()).toBe(false);
    expect(fixture.nativeElement.querySelector('.document-type-select')).toBeNull();

    const browseButton = fixture.nativeElement.querySelector('button.btn-primary');
    expect(browseButton).not.toBeNull();
    expect(browseButton.disabled).toBe(true);
  });

  it('shows the TypesUnavailable state with its retry when the types fetch failed', async () => {
    const { fixture, store } = await setup(GRANT_ONLY, [], () => throwError(() => new Error('offline')));

    expect(store.error()).toBe(true);
    expect(fixture.componentInstance.showTypesUnavailable()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Document:Upload:TypesUnavailable');
    // No file input: without the grant list, a grant-only uploader has nothing it could legally upload.
    expect(fixture.nativeElement.querySelector('input[type="file"]')).toBeNull();
  });

  it('does not show the unavailable state to Documents.Upload, whose untyped upload needs no type list', async () => {
    const { fixture } = await setup(UPLOAD_INTO_ALL, [], () => throwError(() => new Error('offline')));

    expect(fixture.componentInstance.showTypesUnavailable()).toBe(false);
    expect(fixture.nativeElement.querySelector('input[type="file"]')).not.toBeNull();
  });

  it('recovers from the failed fetch through the retry, for this page and every other', async () => {
    // #645 (a): the grant-only uploader's way back. The retry reloads the STORE (#635), so every page that
    // reads it recovers too. Fails twice: once for the store's own first fetch, once for the retryIfFailed()
    // this card makes on arrival (the synchronous stub has already failed by ngOnInit; over real HTTP the
    // first fetch would still be in flight and that retry would be a no-op). Only then is the button the way
    // out.
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of([GRANTED_A]));
    const { fixture } = await setup(GRANT_ONLY, [], getVisible);
    const component = fixture.componentInstance;
    expect(component.showTypesUnavailable()).toBe(true);

    const retry = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(b => b.textContent?.includes('::Refresh'));
    expect(retry).toBeDefined();
    retry!.click();
    fixture.detectChanges();

    expect(getVisible).toHaveBeenCalledTimes(3);
    expect(component.showTypesUnavailable()).toBe(false);
    expect(component.assignableTypes()).toEqual([GRANTED_A]);
    expect(fixture.nativeElement.textContent).not.toContain('Document:Upload:TypesUnavailable');
  });

  it('refuses a drop before a required type is selected with a warning toast, not a silent no-op', async () => {
    const { fixture, uploadSpy, toasterSpy } = await setup(GRANT_ONLY, [GRANTED_A, GRANTED_B]);
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
    const getVisible = vi.fn().mockReturnValueOnce(of([GRANTED_A])).mockReturnValue(of([GRANTED_B]));
    const { fixture, store } = await setup(GRANT_ONLY, [GRANTED_A], getVisible);
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

import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DocumentTypeDto,
  DocumentTypePackService,
  DocumentTypeService,
  DuplicateDetectionScope,
  SlugSuggestionService,
} from '@dignite/ng.vault-extract';
import { DocumentTypesStore } from '../../shared/document-types.store';
import { DocumentTypeListComponent } from './document-type-list.component';

// #635 follow-up: this page is the ONLY place in the SPA that writes the document-type layer, and the
// reading pages (document list, detail, overview, upload card) now share one root-provided
// DocumentTypesStore that fetches once per session instead of once per navigation. So a write here that
// does not invalidate the store leaves those pages stale for the rest of the session: a type created here
// is not offered by the upload picker, an archived one is still offered, and an Upload grant handed out in
// the permission dialog is not seen by the type picker.
//
// The invalidation must be at the write sites and on SUCCESS only — reloading on init or on navigation
// would put back the four redundant fetches the store removed, and reloading after a failed write would
// discard a correct answer for an unchanged layer.
//
// The component is constructed but never rendered: its template needs a live datatable, while every fact
// here is about which calls a handler makes.

const TYPE: DocumentTypeDto = { id: 'type-a', typeCode: 'contract', displayName: 'Contract' };

function setup(options: { confirmDelete?: boolean } = {}) {
  const getVisible = vi.fn().mockReturnValue(of([TYPE]));
  const service = {
    getVisible,
    getDeleted: vi.fn().mockReturnValue(of([])),
    create: vi.fn().mockReturnValue(of(TYPE)),
    update: vi.fn().mockReturnValue(of(TYPE)),
    delete: vi.fn().mockReturnValue(of(void 0)),
    restore: vi.fn().mockReturnValue(of(void 0)),
    getDuplicateScopePreview: vi.fn().mockReturnValue(of({
      currentScope: DuplicateDetectionScope.Layer,
      prospectiveScope: DuplicateDetectionScope.Uploader,
      wouldChange: true,
      willFlagCount: 0,
      willClearCount: 0,
    })),
  };

  TestBed.configureTestingModule({
    imports: [DocumentTypeListComponent],
    providers: [
      provideRouter([]),
      {
        provide: PermissionService,
        useValue: { getGrantedPolicy: () => true },
      },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn() } },
      {
        provide: ConfirmationService,
        useValue: {
          warn: vi
            .fn()
            .mockReturnValue(
              of(options.confirmDelete === false ? Confirmation.Status.reject : Confirmation.Status.confirm),
            ),
        },
      },
      { provide: DocumentTypeService, useValue: service },
      { provide: DocumentTypePackService, useValue: {} },
      { provide: SlugSuggestionService, useValue: { suggest: () => of({ slug: '' }) } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentTypeListComponent);
  const store = TestBed.inject(DocumentTypesStore);
  const reload = vi.spyOn(store, 'reload');

  return { component: fixture.componentInstance, fixture, service, store, reload, getVisible };
}

/** Puts the component into edit mode for TYPE with a valid form, then submits. */
function submitEdit(component: DocumentTypeListComponent): void {
  component.openEdit(TYPE);
  component.form.patchValue({
    typeCode: 'contract',
    displayName: 'Contract',
    description: '',
    confidenceThreshold: 0.8,
    priority: 0,
  });
  component.submit();
}

/** Same as submitEdit(), but for TYPE edited with an explicit duplicateScope (#651). */
function submitEditWithScope(component: DocumentTypeListComponent, duplicateScope: DuplicateDetectionScope): void {
  component.openEdit(TYPE);
  component.form.patchValue({
    typeCode: 'contract',
    displayName: 'Contract',
    description: '',
    confidenceThreshold: 0.8,
    priority: 0,
    duplicateScope,
  });
  component.submit();
}

describe('DocumentTypeListComponent — writes invalidate the shared type store (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('does not reload the store merely by existing', () => {
    // The construction-time fetch the store does for itself is not this page's business; a second one here
    // would be the per-navigation refetch the store exists to remove.
    const { reload } = setup();

    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload it when the page refreshes its own list — that is a read', () => {
    const { component, reload } = setup();

    component.refresh();

    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads it after a successful create', () => {
    const { component, service, reload } = setup();

    component.openCreate();
    component.form.patchValue({
      typeCode: 'invoice',
      displayName: 'Invoice',
      description: '',
      confidenceThreshold: 0.8,
      priority: 0,
    });
    component.submit();

    expect(service.create).toHaveBeenCalled();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does not reload it when the create fails', () => {
    const { component, service, reload } = setup();
    service.create.mockReturnValue(throwError(() => new Error('conflict')));

    component.openCreate();
    component.form.patchValue({
      typeCode: 'invoice',
      displayName: 'Invoice',
      description: '',
      confidenceThreshold: 0.8,
      priority: 0,
    });
    component.submit();

    expect(service.create).toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads it after a successful update', () => {
    const { component, service, reload } = setup();

    submitEdit(component);

    expect(service.update).toHaveBeenCalled();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does not reload it when the update fails', () => {
    const { component, service, reload } = setup();
    service.update.mockReturnValue(throwError(() => new Error('conflict')));

    submitEdit(component);

    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads it after a successful archive', () => {
    const { component, service, reload } = setup();

    component.delete(TYPE);

    expect(service.delete).toHaveBeenCalledWith('type-a');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does not reload it when the archive fails', () => {
    const { component, service, reload } = setup();
    service.delete.mockReturnValue(throwError(() => new Error('in use')));

    component.delete(TYPE);

    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload it when the archive is not confirmed', () => {
    const { component, service, reload } = setup({ confirmDelete: false });

    component.delete(TYPE);

    expect(service.delete).not.toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads it after a successful restore', () => {
    const { component, service, reload } = setup();

    component.restore(TYPE);

    expect(service.restore).toHaveBeenCalledWith('type-a');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('reloads it after a config-pack import, which creates and updates types in bulk', () => {
    const { component, reload } = setup();

    component.onPackImported();

    expect(reload).toHaveBeenCalledTimes(1);
  });
});

describe('DocumentTypeListComponent — the permission dialog invalidates on close (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('reloads the store when the dialog goes from open to closed', () => {
    // ABP's ResourcePermissionManagementComponent exposes only `visible` — there is no "saved" output to
    // distinguish a save from a cancel — so a close is the only signal available, and reloading on it is
    // the conservative reading. This drives the same handler `(visibleChange)` is bound to.
    const { component, reload } = setup();

    component.openPermissions(TYPE);
    expect(reload).not.toHaveBeenCalled();

    component.onPermissionsVisibleChange(false);

    expect(reload).toHaveBeenCalledTimes(1);
    expect(component.permissionsVisible()).toBe(false);
    // Re-opening for another row must not carry the previous target over.
    expect(component.permissionsTarget()).toBeNull();
  });

  it('does not reload when the dialog merely opens', () => {
    const { component, reload } = setup();

    component.openPermissions(TYPE);
    component.onPermissionsVisibleChange(true);

    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload on a close reported while it was already closed', () => {
    // The transition guard. Without it, any `visibleChange(false)` arriving on an already-closed dialog —
    // including the one a freshly bound input can emit — would reload on every visit to this page.
    const { component, reload } = setup();

    component.onPermissionsVisibleChange(false);
    component.onPermissionsVisibleChange(false);

    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads once per close, not once per report', () => {
    const { component, reload } = setup();

    component.openPermissions(TYPE);
    component.onPermissionsVisibleChange(false);
    component.onPermissionsVisibleChange(false);
    component.onPermissionsVisibleChange(false);

    expect(reload).toHaveBeenCalledTimes(1);
  });
});

// Guards the shape the coordinator's fix depends on: reload() is a method on the store, so a spy on the
// injected singleton is what the facts above observe. If the store ever stopped being a singleton, every
// assertion above would pass while the readers held a different instance.
describe('DocumentTypesStore is one instance for writer and readers (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('hands the write page the same store the reading pages inject', () => {
    const { component, store } = setup();

    expect(TestBed.inject(DocumentTypesStore)).toBe(store);
    expect((component as unknown as { documentTypes: DocumentTypesStore }).documentTypes).toBe(store);
  });
});

// #651: switching DuplicateScope on save enqueues automatic reconciliation of existing review flags, so the
// admin previews the consequence before the update request goes out — but only when the scope actually
// changes on an EXISTING type. Creating a type never previews (no documents exist yet to re-evaluate).
describe('DocumentTypeListComponent — duplicate-detection scope pre-save preview (#651)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('defaults duplicateScope to Layer on create', () => {
    const { component } = setup();

    component.openCreate();

    expect(component.form.controls.duplicateScope.value).toBe(DuplicateDetectionScope.Layer);
  });

  it('defaults duplicateScope to Layer when editing a type that predates the setting', () => {
    const { component } = setup();

    component.openEdit(TYPE); // TYPE carries no duplicateScope

    expect(component.form.controls.duplicateScope.value).toBe(DuplicateDetectionScope.Layer);
  });

  it('never previews on create, even though the form carries a duplicateScope value', () => {
    const { component, service } = setup();

    component.openCreate();
    component.form.patchValue({
      typeCode: 'invoice',
      displayName: 'Invoice',
      description: '',
      confidenceThreshold: 0.8,
      priority: 0,
      duplicateScope: DuplicateDetectionScope.Uploader,
    });
    component.submit();

    expect(service.getDuplicateScopePreview).not.toHaveBeenCalled();
    expect(service.create).toHaveBeenCalled();
  });

  it('saves straight through, with no preview call, when duplicateScope is left unchanged', () => {
    const { component, service } = setup();

    submitEdit(component); // does not touch duplicateScope; stays at TYPE's default (Layer)

    expect(service.getDuplicateScopePreview).not.toHaveBeenCalled();
    expect(service.update).toHaveBeenCalled();
  });

  // #651 review correction: both directions re-evaluate every fingerprinted document and either one can
  // both flag and clear (a narrowing switch can also newly flag a stale first-upload gap), so there is
  // exactly one message with both counts — no direction branch, no direction-specific wording.
  it('previews and confirms with the single message and both counts before saving a scope switch', () => {
    const { component, service } = setup();
    service.getDuplicateScopePreview.mockReturnValue(of({
      currentScope: DuplicateDetectionScope.Layer,
      prospectiveScope: DuplicateDetectionScope.Uploader,
      wouldChange: true,
      willFlagCount: 3,
      willClearCount: 2,
    }));

    submitEditWithScope(component, DuplicateDetectionScope.Uploader);

    expect(service.getDuplicateScopePreview).toHaveBeenCalledWith('type-a', DuplicateDetectionScope.Uploader);
    const confirmation = TestBed.inject(ConfirmationService) as unknown as { warn: ReturnType<typeof vi.fn> };
    expect(confirmation.warn).toHaveBeenCalledWith(
      '::DocumentType:DuplicateScope:Preview:Message',
      '::DocumentType:DuplicateScope:Preview:Title',
      expect.objectContaining({ messageLocalizationParams: ['3', '2'] }),
    );
    expect(service.update).toHaveBeenCalled();
  });

  it('does not save when the pre-save confirmation is cancelled', () => {
    const { component, service } = setup();
    const confirmation = TestBed.inject(ConfirmationService) as unknown as { warn: ReturnType<typeof vi.fn> };
    confirmation.warn.mockReturnValue(of(Confirmation.Status.reject));

    submitEditWithScope(component, DuplicateDetectionScope.Uploader);

    expect(service.getDuplicateScopePreview).toHaveBeenCalled();
    expect(service.update).not.toHaveBeenCalled();
  });

  it('does not save when the preview call itself fails', () => {
    const { component, service } = setup();
    service.getDuplicateScopePreview.mockReturnValue(throwError(() => new Error('server error')));

    submitEditWithScope(component, DuplicateDetectionScope.Uploader);

    expect(service.update).not.toHaveBeenCalled();
  });
});

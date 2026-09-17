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

import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentStatisticsService,
  DocumentTypeDto,
  DocumentTypeService,
  DocumentUploadService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { DocumentOverviewComponent } from './document-overview.component';

// #632: the overview's statistics card is a WHOLE-LAYER aggregate and DocumentStatisticsAppService now
// requires Documents.ReadAll. Without it the card is hidden and — the part that actually matters — the GET is
// never fired, because it would 403.

const RESOURCES = EXTRACT_PERMISSIONS.DocumentTypes.Resources;

const TYPE_WITH_EDIT: DocumentTypeDto = {
  id: 'type-a',
  typeCode: 'contract',
  displayName: 'Contract',
  resourcePermissions: { [RESOURCES.Edit]: true },
};

const TYPE_WITHOUT_EDIT: DocumentTypeDto = {
  id: 'type-b',
  typeCode: 'invoice',
  displayName: 'Invoice',
  resourcePermissions: { [RESOURCES.Read]: true },
};

function setup(
  grantedPolicies: Set<string>,
  types: DocumentTypeDto[] = [TYPE_WITH_EDIT],
  getVisible: () => Observable<DocumentTypeDto[]> = () => of(types),
) {
  const statisticsSpy = vi.fn().mockReturnValue(of({ totalCount: 7 }));

  TestBed.configureTestingModule({
    imports: [DocumentOverviewComponent],
    providers: [
      provideRouter([]),
      {
        provide: PermissionService,
        useValue: { getGrantedPolicy: (key: string) => grantedPolicies.has(key) },
      },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn() } },
      { provide: DocumentStatisticsService, useValue: { get: statisticsSpy } },
      { provide: DocumentTypeService, useValue: { getVisible } },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
      { provide: DocumentUploadService, useValue: { upload: vi.fn() } },
    ],
  });

  const component = TestBed.createComponent(DocumentOverviewComponent).componentInstance;
  component.ngOnInit();
  return { component, statisticsSpy };
}

const READ_ALL = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
]);

const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

describe('DocumentOverviewComponent — statistics card (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders and fetches the card with ReadAll', () => {
    const { component, statisticsSpy } = setup(READ_ALL);

    expect(component.canReadAll).toBe(true);
    expect(statisticsSpy).toHaveBeenCalled();
    expect(component.stats()?.totalCount).toBe(7);
    expect(component.statsLoading()).toBe(false);
  });

  it('hides the card and never fires the request without ReadAll', () => {
    const { component, statisticsSpy } = setup(ENTRY_ONLY);

    expect(component.canReadAll).toBe(false);
    expect(statisticsSpy).not.toHaveBeenCalled();
    // Not a skeleton either: a card that will never arrive must not sit there loading forever.
    expect(component.statsLoading()).toBe(false);
    expect(component.statsError()).toBe(false);
  });
});

describe('DocumentOverviewComponent — needs-review quick link (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('is offered to a caller holding only an Edit grant on some type', () => {
    const { component } = setup(ENTRY_ONLY, [TYPE_WITH_EDIT]);

    expect(component.canConfirmClassification).toBe(false);
    expect(component.canReviewAnyType()).toBe(true);
  });

  it('is hidden when no visible type carries an Edit grant', () => {
    const { component } = setup(ENTRY_ONLY, [TYPE_WITHOUT_EDIT]);

    expect(component.canReviewAnyType()).toBe(false);
  });

  it('is offered module-wide regardless of the type list', () => {
    const { component } = setup(
      new Set([EXTRACT_PERMISSIONS.Documents.ConfirmClassification]),
      [TYPE_WITHOUT_EDIT],
    );

    expect(component.canReviewAnyType()).toBe(true);
  });
});

// #635 decision 7: this page used to fetch the visible types itself and hand the upload card three inputs —
// the list, a loading flag and an unavailable flag — because an empty list alone could not tell the card
// which of the three states it was in. Both the fetch and the inputs are gone.
describe('DocumentOverviewComponent — the types come from the store (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads the quick-link types from the store, on one fetch shared with the upload card', () => {
    const getVisible = vi.fn().mockReturnValue(of([TYPE_WITH_EDIT]));
    const { component } = setup(ENTRY_ONLY, [TYPE_WITH_EDIT], getVisible);

    expect(component.documentTypes.value()).toEqual([TYPE_WITH_EDIT]);
    // One call, although both this page and the upload card it hosts need the answer.
    expect(getVisible).toHaveBeenCalledTimes(1);
  });

  it('keeps the type section on screen while the answer is still in flight', () => {
    // Showing "no document types yet" before the fetch lands would be a claim the page cannot make.
    const pending = new Subject<DocumentTypeDto[]>();
    const { component } = setup(ENTRY_ONLY, [], () => pending.asObservable());

    expect(component.documentTypes.isLoading()).toBe(true);
    expect(component.showTypeSection()).toBe(true);
  });

  it('surfaces a failed fetch as an error state rather than as an empty list', () => {
    const { component } = setup(ENTRY_ONLY, [], () => throwError(() => new Error('offline')));

    expect(component.documentTypes.error()).toBe(true);
    expect(component.documentTypes.value()).toEqual([]);
  });
});

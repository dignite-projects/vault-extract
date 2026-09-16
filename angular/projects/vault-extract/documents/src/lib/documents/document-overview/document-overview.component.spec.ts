import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { of } from 'rxjs';
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

function setup(grantedPolicies: Set<string>, types: DocumentTypeDto[] = [TYPE_WITH_EDIT]) {
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
      { provide: DocumentTypeService, useValue: { getVisible: () => of(types) } },
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

    expect(component.canReview).toBe(false);
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

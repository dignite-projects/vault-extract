import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Observable, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CabinetService,
  DocumentDto,
  DocumentPipelineRunService,
  DocumentReviewReasons,
  DocumentRightsDto,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
  FieldDefinitionService,
} from '@dignite/ng.vault-extract';
import { DocumentDetailComponent } from './document-detail.component';

// #635 decision 5: every affordance on this page reads the verdict the server sent down with the document.
// The page used to re-derive it from the caller's grants and the document's type, which could not express
// ownership and lost every action on a document whose type had been archived.
//
// #635 decision 2 also splits the page's single old gate in two: the EDIT family (confirm / reclassify /
// re-recognize / re-extract / field editing / Markdown correction / cabinet re-filing, all of which an
// uploader may run on their own document) and the REVIEW family (allow duplicate / resolve warnings /
// reject), which clear a blocking review reason and are therefore closed to an owner acting alone.
//
// The component is constructed but never rendered where the gate under test is a computed on the instance;
// the facts about which BUTTONS appear render the template.

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

/** Everything admitted — a module-wide operator, or an Edit-grant holder on this document's type. */
const FULL: DocumentRightsDto = {
  canRead: true,
  canEdit: true,
  canReview: true,
  canDelete: true,
  canRestore: true,
  canRetry: true,
};
/**
 * #635 decision 8's day-one persona: the uploader of this document, holding no per-type grant at all. May
 * read, edit, delete, restore and retry their own upload — and may NOT clear its blocking review reasons.
 */
const OWNER: DocumentRightsDto = { ...FULL, canReview: false };
/** Visible, and nothing more. */
const READ_ONLY: DocumentRightsDto = {
  canRead: true,
  canEdit: false,
  canReview: false,
  canDelete: false,
  canRestore: false,
  canRetry: false,
};

/** A loaded, already-extracted document — the state in which the edit affordances are offered. */
function documentOf(
  rights: DocumentRightsDto,
  documentTypeCode: string | null = 'contract',
  extra: Partial<DocumentDto> = {},
): DocumentDto {
  return {
    id: 'doc-1',
    documentTypeCode,
    markdown: '# body',
    reviewReasons: DocumentReviewReasons.UnresolvedClassification,
    rights,
    ...extra,
  } as DocumentDto;
}

function setup(
  grantedPolicies: Set<string>,
  getVisible: () => Observable<DocumentTypeDto[]> = () => of([TYPE_A, TYPE_B]),
) {
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
      { provide: DocumentTypeService, useValue: { getVisible } },
      { provide: FieldDefinitionService, useValue: { getFieldTypes: () => of([]), getList: () => of([]) } },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentDetailComponent);
  return { component: fixture.componentInstance, fixture };
}

const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
]);

const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

describe('DocumentDetailComponent — the rights come with the document (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers edit and delete when the row says so', () => {
    const { component } = setup(ENTRY_ONLY);

    component.document.set(documentOf(FULL));
    expect(component.canEdit()).toBe(true);
    expect(component.canDelete()).toBe(true);
  });

  it('withholds them when the row says so, whatever the caller holds module-wide', () => {
    // The module-wide half is already folded into the server's verdict, so a permission the caller holds is
    // never a reason to override the row.
    const { component } = setup(MODULE_WIDE);

    component.document.set(documentOf(READ_ONLY));
    expect(component.canEdit()).toBe(false);
    expect(component.canDelete()).toBe(false);
  });

  it('denies everything before a document is loaded', () => {
    const { component } = setup(MODULE_WIDE);

    expect(component.canEdit()).toBe(false);
    expect(component.canDelete()).toBe(false);
    expect(component.canReview()).toBe(false);
    expect(component.canRetry()).toBe(false);
  });

  it('answers the same on an untyped document — the verdict does not need a type', () => {
    // The old client-side table could not express this: an untyped document (unclassified, failed
    // classification, container) matched no type, so it fell back to the module-wide half and its uploader
    // saw nothing. The server now answers it directly.
    const { component } = setup(ENTRY_ONLY);

    component.document.set(documentOf(OWNER, null));
    expect(component.canEdit()).toBe(true);
    expect(component.canDelete()).toBe(true);
  });

  it('answers the same on a document whose type is not in the visible list', () => {
    // An archived type: the client-side table keyed by code against the ACTIVE types and so hid every
    // action the API would have admitted.
    const { component } = setup(ENTRY_ONLY);

    component.document.set(documentOf(FULL, 'since-archived'));
    expect(component.canEdit()).toBe(true);
    expect(component.canDelete()).toBe(true);
    expect(component.canRetry()).toBe(true);
  });

  it('carries the whole edit family with it', () => {
    const { component } = setup(ENTRY_ONLY);
    component.isLoading.set(false);

    component.document.set(documentOf(FULL));
    expect(component.needsClassification()).toBe(true);
    expect(component.canRerecognize()).toBe(true);
    expect(component.canReextractFields()).toBe(true);
    expect(component.canEditMarkdown()).toBe(true);

    component.document.set(documentOf(READ_ONLY));
    expect(component.canRerecognize()).toBe(false);
    expect(component.canReextractFields()).toBe(false);
    expect(component.canEditMarkdown()).toBe(false);
  });
});

describe('DocumentDetailComponent — review is narrower than edit (#635 decision 2)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function reviewButtons(fixture: ReturnType<typeof setup>['fixture']): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll('button'))
      .map(b => (b as HTMLElement).textContent?.trim() ?? '')
      .filter(text => text.length > 0);
  }

  it('offers the three review resolutions to a reviewer', () => {
    const { component, fixture } = setup(ENTRY_ONLY);
    component.isLoading.set(false);
    component.document.set(
      documentOf(FULL, 'contract', {
        requiresReview: true,
        reviewReasons: DocumentReviewReasons.DuplicateSuspected,
        reviewReasonDetails: [],
      }),
    );
    fixture.detectChanges();

    const labels = reviewButtons(fixture).join('|');
    expect(labels).toContain('Document:Review:AllowDuplicate');
    expect(labels).toContain('Document:Review:Reject');
  });

  it('withholds them from the uploader of the document, who may still edit it', () => {
    // The adversarial case the exclusion is for: a duplicate invoice is exactly what the review queue
    // exists to catch, so its uploader does not get to release it.
    const { component, fixture } = setup(ENTRY_ONLY);
    component.isLoading.set(false);
    component.document.set(
      documentOf(OWNER, 'contract', {
        requiresReview: true,
        reviewReasons: DocumentReviewReasons.DuplicateSuspected,
        reviewReasonDetails: [],
      }),
    );
    fixture.detectChanges();

    expect(component.canEdit()).toBe(true);
    expect(component.canReview()).toBe(false);

    const labels = reviewButtons(fixture).join('|');
    expect(labels).not.toContain('Document:Review:AllowDuplicate');
    expect(labels).not.toContain('Document:Review:Reject');
  });

  it('still offers the owner the edit-family remediation on the same banner', () => {
    const { component, fixture } = setup(ENTRY_ONLY);
    component.isLoading.set(false);
    component.document.set(
      documentOf(OWNER, null, {
        requiresReview: true,
        reviewReasons: DocumentReviewReasons.UnresolvedClassification,
        reviewReasonDetails: [],
      }),
    );
    fixture.detectChanges();

    expect(component.needsClassification()).toBe(true);
    expect(reviewButtons(fixture).join('|')).toContain('Document:ConfirmClassification');
  });
});

describe('DocumentDetailComponent — retry follows the row (#635 decision 2)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('exposes canRetry straight from the row', () => {
    const { component } = setup(ENTRY_ONLY);

    component.document.set(documentOf(FULL));
    expect(component.canRetry()).toBe(true);

    // A Read-only caller who happens to hold module-wide Pipelines.Retry used to reach this button on any
    // readable document, around the per-type Edit gate its neighbour "re-recognize" already had.
    component.document.set(documentOf(READ_ONLY));
    expect(component.canRetry()).toBe(false);
  });
});

describe('DocumentDetailComponent — classify picker (#632)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists every type for a ConfirmClassification holder', () => {
    expect(setup(MODULE_WIDE).component.assignableTypes()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted target types otherwise', () => {
    expect(setup(ENTRY_ONLY).component.assignableTypes()).toEqual([TYPE_A]);
  });

  it('never pre-selects a type the caller may not assign', () => {
    const { component } = setup(ENTRY_ONLY);

    component.document.set(documentOf(FULL, 'invoice'));
    component.openClassifyDialog();
    expect(component.selectedTypeId()).toBe('');

    component.closeClassifyDialog();
    component.document.set(documentOf(FULL, 'contract'));
    component.openClassifyDialog();
    expect(component.selectedTypeId()).toBe('type-a');
  });

  it('reads the picker options from the shared store, not from a fetch of its own', () => {
    const getVisible = vi.fn().mockReturnValue(of([TYPE_A, TYPE_B]));
    const { component } = setup(MODULE_WIDE, getVisible);

    expect(component.documentTypes.value()).toEqual([TYPE_A, TYPE_B]);
    expect(getVisible).toHaveBeenCalledTimes(1);
  });
});

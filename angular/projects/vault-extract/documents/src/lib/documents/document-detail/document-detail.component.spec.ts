import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { HttpErrorReporterService, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Observable, Subject, of, throwError } from 'rxjs';
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
  FieldDefinitionDto,
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
  documentService: Record<string, unknown> = {},
  fieldDefinitionService: Record<string, unknown> = { getFieldTypes: () => of([]), getList: () => of([]) },
) {
  const toaster = { success: vi.fn(), error: vi.fn(), warn: vi.fn(), info: vi.fn() };
  const reporter = { reportError: vi.fn() };

  TestBed.configureTestingModule({
    imports: [DocumentDetailComponent],
    providers: [
      provideRouter([]),
      {
        provide: PermissionService,
        useValue: { getGrantedPolicy: (key: string) => grantedPolicies.has(key) },
      },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: toaster },
      { provide: ConfirmationService, useValue: { warn: vi.fn().mockReturnValue(of(null)) } },
      { provide: DocumentService, useValue: documentService },
      { provide: DocumentPipelineRunService, useValue: { getList: () => of([]) } },
      { provide: DocumentTypeService, useValue: { getVisible } },
      { provide: FieldDefinitionService, useValue: fieldDefinitionService },
      { provide: HttpErrorReporterService, useValue: reporter },
      { provide: CabinetService, useValue: { getList: () => of([]) } },
    ],
  });

  const fixture = TestBed.createComponent(DocumentDetailComponent);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  return { component: fixture.componentInstance, fixture, toaster, navigate, reporter };
}

const MODULE_WIDE = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
]);

const ENTRY_ONLY = new Set<string>([EXTRACT_PERMISSIONS.Documents.Default]);

/** Entry plus the role-level right to assign any type, which #648 additionally requires of "重新分类". */
const ENTRY_AND_CONFIRM = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
]);

/** The same right through the other member of DeclareType's role-level set. */
const ENTRY_AND_UPLOAD_ALL = new Set<string>([
  EXTRACT_PERMISSIONS.Documents.Default,
  EXTRACT_PERMISSIONS.Documents.Upload,
]);

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
    // #648 adds a second term to re-recognize only, so the caller here holds the role-level assign right and
    // the row stays the thing under test. The added term is covered on its own below.
    const { component } = setup(ENTRY_AND_CONFIRM);
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

// #645: the confirm / reclassify target picker mirrors the server's DeclareType row — every type for
// ConfirmClassification OR Documents.Upload, otherwise the types carrying an Upload grant.
describe('DocumentDetailComponent — classify picker (#632, #645)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists every type for a ConfirmClassification holder', () => {
    expect(setup(MODULE_WIDE).component.assignableTypes()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists every type for a Documents.Upload holder without ConfirmClassification', () => {
    const uploadIntoAll = new Set<string>([
      EXTRACT_PERMISSIONS.Documents.Default,
      EXTRACT_PERMISSIONS.Documents.Upload,
    ]);

    expect(setup(uploadIntoAll).component.assignableTypes()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted target types for a grant-only caller', () => {
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

// #648: the classifier picks the target type, so "重新分类" additionally needs the role-level right to assign
// any type — the same OR the picker above applies, through the one shared helper.
describe('DocumentDetailComponent — AI re-classification needs the assign-any-type right (#648)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function render(grantedPolicies: Set<string>) {
    const { component, fixture } = setup(grantedPolicies);
    component.isLoading.set(false);
    component.document.set(documentOf(FULL));
    fixture.detectChanges();
    return { component, text: fixture.nativeElement.textContent as string };
  }

  it('hides the button from a caller who may edit the document but holds neither permission', () => {
    // The Issue's caller: entry plus an Upload grant on one type. The row admits the edit family, but the
    // classifier could move the document into any type of the layer.
    const { component, text } = render(ENTRY_ONLY);

    expect(component.canEdit()).toBe(true);
    expect(component.canRerecognize()).toBe(false);
    expect(text).not.toContain('Document:Rerecognize');
  });

  it('shows it to a ConfirmClassification holder', () => {
    const { component, text } = render(ENTRY_AND_CONFIRM);

    expect(component.canRerecognize()).toBe(true);
    expect(text).toContain('Document:Rerecognize');
  });

  it('shows it to a Documents.Upload holder', () => {
    const { component, text } = render(ENTRY_AND_UPLOAD_ALL);

    expect(component.canRerecognize()).toBe(true);
    expect(text).toContain('Document:Rerecognize');
  });
});

// #635 (after the #638 review): an edit- or review-family call whose result the caller may not read comes back
// redacted to `{ id, rights }`. The write happened; the body is not a document to show.
describe('DocumentDetailComponent — a mutation result the caller may not read (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  /** What the server returns when the write succeeded but the caller may no longer read the document. */
  const REDACTED: DocumentDto = {
    id: 'doc-1',
    rights: { ...READ_ONLY, canRead: false },
  } as DocumentDto;

  it('leaves the page after a reclassification out of the read scope, without rendering or reloading', () => {
    // The real case: an Edit grant on the current type and an Upload grant on the target, but no Read on
    // the target. The page used to ignore this body and reload — a GetAsync the caller is now refused.
    const get = vi.fn();
    const confirmClassification = vi.fn().mockReturnValue(of(REDACTED));
    const { component, toaster, navigate } = setup(ENTRY_ONLY, undefined, { get, confirmClassification });
    const before = documentOf(FULL);
    component.document.set(before);
    component.selectedTypeId.set('type-b');

    component.submitClassify();

    expect(confirmClassification).toHaveBeenCalled();
    expect(component.document()).toBe(before);
    expect(get).not.toHaveBeenCalled();
    expect(toaster.info).toHaveBeenCalledWith('::Document:SavedOutOfView', '::Success');
    expect(toaster.success).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/documents/list']);
  });

  it('does not put a redacted body into state on a call that renders its response', () => {
    const updateCabinet = vi.fn().mockReturnValue(of(REDACTED));
    const { component, navigate } = setup(ENTRY_ONLY, undefined, { updateCabinet });
    const before = documentOf(FULL);
    component.document.set(before);

    component.saveCabinet();

    expect(component.document()).toBe(before);
    expect(navigate).toHaveBeenCalledWith(['/documents/list']);
  });

  it('keeps the page, and the new body, when the caller can still read the result', () => {
    // The counter-case: without it the facts above would pass on a guard that always leaves.
    const after = documentOf(FULL, 'contract', { cabinetId: 'cabinet-1' });
    const updateCabinet = vi.fn().mockReturnValue(of(after));
    const { component, toaster, navigate } = setup(ENTRY_ONLY, undefined, { updateCabinet });
    component.document.set(documentOf(FULL));

    component.saveCabinet();

    expect(component.document()).toBe(after);
    expect(navigate).not.toHaveBeenCalled();
    expect(toaster.success).toHaveBeenCalledWith('::Document:CabinetUpdated', '::Success');
  });
});

// #635 (after the #638 review): an uploader whose own document is held for review keeps read and delete, and
// loses edit and retry. The page tells them why the actions are gone — from the server's `isBlocking` and
// `rights` only, never from a client-side copy of which reasons block.
describe('DocumentDetailComponent — "waiting for a reviewer" (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const LOCKED_OWNER: DocumentRightsDto = { ...OWNER, canEdit: false, canRetry: false };

  function render(rights: DocumentRightsDto, isBlocking: boolean) {
    const { component, fixture } = setup(ENTRY_ONLY);
    component.isLoading.set(false);
    component.document.set(
      documentOf(rights, 'contract', {
        requiresReview: true,
        reviewReasons: DocumentReviewReasons.DuplicateSuspected,
        reviewReasonDetails: [
          { reason: DocumentReviewReasons.DuplicateSuspected, isBlocking, duplicateCandidates: [] },
        ],
      }),
    );
    fixture.detectChanges();
    return { component, text: fixture.nativeElement.textContent as string };
  }

  it('is shown for a blocked document the caller may neither edit nor review', () => {
    const { component, text } = render(LOCKED_OWNER, true);

    expect(component.waitingForReviewer()).toBe(true);
    expect(text).toContain('Document:Review:WaitingForReviewer');
  });

  it('is hidden when the caller may still edit it', () => {
    // An uploader whose document is blocked only on classification: they may confirm or reclassify it, so
    // there is something they can do.
    const { text } = render({ ...LOCKED_OWNER, canEdit: true }, true);

    expect(text).not.toContain('Document:Review:WaitingForReviewer');
  });

  it('is hidden when the caller may review it', () => {
    const { text } = render({ ...LOCKED_OWNER, canReview: true }, true);

    expect(text).not.toContain('Document:Review:WaitingForReviewer');
  });

  it('is hidden when nothing is blocking', () => {
    const { text } = render(LOCKED_OWNER, false);

    expect(text).not.toContain('Document:Review:WaitingForReviewer');
  });

  // #648: a per-reason hint is an instruction. On a blocking reason the two lines are complements — whoever
  // reads the hint does not read the waiting line, and the other way round.
  it('replaces the per-reason hints for a caller who may neither edit nor review', () => {
    const { text } = render(LOCKED_OWNER, true);

    expect(text).not.toContain('Document:Review:Hint:DuplicateSuspected');
    expect(text).toContain('Document:Review:WaitingForReviewer');
  });

  it('shows the per-reason hints to a caller who may edit it', () => {
    const { text } = render({ ...LOCKED_OWNER, canEdit: true }, true);

    expect(text).toContain('Document:Review:Hint:DuplicateSuspected');
  });

  it('shows the per-reason hints to a caller who may review it', () => {
    const { text } = render({ ...LOCKED_OWNER, canReview: true }, true);

    expect(text).toContain('Document:Review:Hint:DuplicateSuspected');
  });
});

// #651 §7 (previously #635): the duplicate-candidate panel is narrowed by the caller's read scope, so an
// uploader whose document is flagged as a duplicate may receive no candidate at all. HiddenDuplicateCandidateCount
// tells "candidates exist, outside your view" apart from "nothing collides any more".
describe('DocumentDetailComponent — an empty duplicate panel (#651 §7)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function renderDuplicate(duplicateCandidates: unknown, hiddenDuplicateCandidateCount?: number) {
    const { component, fixture } = setup(ENTRY_ONLY);
    component.isLoading.set(false);
    component.document.set(
      documentOf(FULL, 'contract', {
        requiresReview: true,
        reviewReasons: DocumentReviewReasons.DuplicateSuspected,
        reviewReasonDetails: [
          {
            reason: DocumentReviewReasons.DuplicateSuspected,
            isBlocking: true,
            duplicateCandidates,
            hiddenDuplicateCandidateCount,
          },
        ] as DocumentDto['reviewReasonDetails'],
      }),
    );
    fixture.detectChanges();
    return fixture.nativeElement.textContent as string;
  }

  it('says an administrator must resolve it when candidates exist but are hidden by the read scope', () => {
    expect(renderDuplicate([], 2)).toContain('Document:ReviewReason:HiddenDuplicateCandidates');
  });

  it('says nothing collides any more when no candidates are visible and none are hidden, whether the list is empty or absent', () => {
    expect(renderDuplicate([], 0)).toContain('Document:ReviewReason:NoRemainingDuplicateCandidates');

    TestBed.resetTestingModule();
    expect(renderDuplicate(undefined, undefined)).toContain('Document:ReviewReason:NoRemainingDuplicateCandidates');
  });

  it('lists the candidates instead when there are some, regardless of the hidden count', () => {
    const text = renderDuplicate([{ id: 'doc-2', title: 'Invoice 42' }], 3);

    expect(text).toContain('Invoice 42');
    expect(text).not.toContain('Document:ReviewReason:HiddenDuplicateCandidates');
    expect(text).not.toContain('Document:ReviewReason:NoRemainingDuplicateCandidates');
  });
});

// #639 review, finding 4: only an EXPLICIT "not readable" leaves the page. rightsOf() fails closed, which is
// right for which buttons to show and wrong for navigating someone away.
describe('DocumentDetailComponent — a mutation result without a verdict (#639 review)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('stays on the page and applies the body when the response carries no rights at all', () => {
    const after = { id: 'doc-1', documentTypeCode: 'contract', cabinetId: 'cabinet-1' } as DocumentDto;
    const updateCabinet = vi.fn().mockReturnValue(of(after));
    const { component, navigate } = setup(ENTRY_ONLY, undefined, { updateCabinet });
    component.document.set(documentOf(FULL));

    component.saveCabinet();

    expect(navigate).not.toHaveBeenCalled();
    expect(component.document()).toBe(after);
  });
});

// #639 review, finding 2: the page's own fetch serves the poll, Refresh, and the reload after re-recognize /
// re-extract / retry. A re-recognition can move the document into a type the caller may not read, and the next
// fetch is then refused. That refusal is an answer, not a failure — every other error is still a failure.
describe('DocumentDetailComponent — the document fetch is refused or fails (#639 review)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('leaves for the list with its own message when a poll is refused with 403', () => {
    const get = vi.fn().mockReturnValue(throwError(() => new HttpErrorResponse({ status: 403 })));
    const { component, toaster, navigate, reporter } = setup(ENTRY_ONLY, undefined, { get });
    const before = documentOf(FULL);
    component.document.set(before);
    (component as unknown as { documentId: string }).documentId = 'doc-1';

    component['pollReload']();

    expect(get).toHaveBeenCalledWith('doc-1', { skipHandleError: true });
    expect(component.document()).toBe(before);
    // Not the mutation message: on a poll, no operation just completed.
    expect(toaster.info).toHaveBeenCalledWith('::Document:NoLongerVisible', undefined);
    expect(toaster.info).not.toHaveBeenCalledWith('::Document:SavedOutOfView', expect.anything());
    expect(navigate).toHaveBeenCalledWith(['/documents/list']);
    // ABP's global modal is exactly what this path exists to avoid.
    expect(reporter.reportError).not.toHaveBeenCalled();
  });

  it('reports any other error on an explicit load, and stays on the page', () => {
    const failure = new HttpErrorResponse({ status: 500 });
    const get = vi.fn().mockReturnValue(throwError(() => failure));
    const { component, navigate, reporter } = setup(ENTRY_ONLY, undefined, { get });
    const before = documentOf(FULL);
    component.document.set(before);
    (component as unknown as { documentId: string }).documentId = 'doc-1';

    component.refresh();

    // The same call RestService.handleError makes without skipHandleError, so ABP's theme shows what it
    // always showed.
    expect(reporter.reportError).toHaveBeenCalledWith(failure);
    expect(navigate).not.toHaveBeenCalled();
    expect(component.document()).toBe(before);
    expect(component.isLoading()).toBe(false);
  });

  it('does not retry a failed type store from a background poll tick', () => {
    // Finding 1's other half: the explicit paths heal the store, the poll must not — against a persistent
    // outage it would turn every tick into a retry.
    const getVisible = vi.fn().mockReturnValue(throwError(() => new Error('offline')));
    const get = vi.fn().mockReturnValue(of(documentOf(FULL)));
    const { component } = setup(ENTRY_ONLY, getVisible, { get });
    (component as unknown as { documentId: string }).documentId = 'doc-1';
    expect(component.documentTypes.error()).toBe(true);

    component['pollReload']();

    expect(getVisible).toHaveBeenCalledTimes(1);
  });

  it('retries a failed type store from the explicit Refresh', () => {
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of([TYPE_A, TYPE_B]));
    const get = vi.fn().mockReturnValue(of(documentOf(FULL)));
    const { component } = setup(ENTRY_ONLY, getVisible, { get });
    (component as unknown as { documentId: string }).documentId = 'doc-1';

    component.refresh();

    expect(getVisible).toHaveBeenCalledTimes(2);
    expect(component.documentTypes.value()).toEqual([TYPE_A, TYPE_B]);
  });
});

// #639 review, finding 3: a field-schema response must never land on a document whose type has moved on.
// Two guards, one per way a request is overtaken — each fact below is red with its own guard removed.
describe('DocumentDetailComponent — field-schema responses arrive out of order (#639 review)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const SCHEMA_A: FieldDefinitionDto[] = [{ id: 'f-a', name: 'contractNo' } as FieldDefinitionDto];
  const SCHEMA_B: FieldDefinitionDto[] = [{ id: 'f-b', name: 'invoiceNo' } as FieldDefinitionDto];

  function schemaSetup(...responses: Subject<FieldDefinitionDto[]>[]) {
    const getList = vi.fn();
    for (const response of responses) {
      getList.mockReturnValueOnce(response.asObservable());
    }
    const { component } = setup(ENTRY_ONLY, undefined, {}, { getFieldTypes: () => of([]), getList });
    const load = (code: string) => component['loadFieldDefinitions'](code, [TYPE_A, TYPE_B]);
    return { component, getList, load };
  }

  it('keeps B\'s schema when A\'s slower response arrives after a reclassification to B', () => {
    const a = new Subject<FieldDefinitionDto[]>();
    const b = new Subject<FieldDefinitionDto[]>();
    const { component, load } = schemaSetup(a, b);

    component.document.set(documentOf(FULL, 'contract'));
    load('contract');
    component.document.set(documentOf(FULL, 'invoice'));
    load('invoice');

    b.next(SCHEMA_B);
    a.next(SCHEMA_A);

    expect(component.fieldDefinitions()).toEqual(SCHEMA_B);
  });

  it('drops a response for a type the document has left, before any newer request exists', () => {
    // The gap the cancellation cannot cover: a poll changed the type, and the effect that would issue the
    // next request has not run yet — so nothing has cancelled this one. Only the arrival check protects it.
    const a = new Subject<FieldDefinitionDto[]>();
    const { component, load } = schemaSetup(a);

    component.document.set(documentOf(FULL, 'contract'));
    load('contract');
    component.document.set(documentOf(FULL, 'invoice'));

    a.next(SCHEMA_A);

    expect(component.fieldDefinitions()).toEqual([]);
  });

  it('keeps the newer answer when two requests for the SAME type race', () => {
    // The gap the arrival check cannot cover: a Refresh re-asks for the type the document still has, so a
    // stale earlier response passes the type check. Only the cancellation protects it.
    const first = new Subject<FieldDefinitionDto[]>();
    const second = new Subject<FieldDefinitionDto[]>();
    const { component, load } = schemaSetup(first, second);
    const edited: FieldDefinitionDto[] = [{ id: 'f-a2', name: 'contractNumber' } as FieldDefinitionDto];

    component.document.set(documentOf(FULL, 'contract'));
    load('contract');
    load('contract');

    second.next(edited);
    first.next(SCHEMA_A);

    expect(component.fieldDefinitions()).toEqual(edited);
  });
});

import { describe, expect, it } from 'vitest';
import { DocumentLifecycleStatus } from '@dignite/vault-extract';

import { DocumentListFilter, toExportDocumentsInput } from './export-current-view';

// #496: "export current view" is only honest if the export input carries every filter the list applied.
// These pin the two ways it could lie: dropping a filter the list applied (file wider than the screen), or
// inventing one the list did not (file narrower).

const TEMPLATE_ID = 'ffffffff-ffff-ffff-ffff-ffffffffffff';

describe('toExportDocumentsInput', () => {
  it('carries every filter the list applied', () => {
    const filter: DocumentListFilter = {
      documentTypeCode: 'invoice.general',
      cabinetId: 'cab-1',
      originDocumentId: 'origin-1',
      lifecycleStatus: DocumentLifecycleStatus.Ready,
      hasReviewReasons: true,
      fieldFilters: [{ name: 'amount', value: '100' }],
    };

    expect(toExportDocumentsInput(TEMPLATE_ID, filter)).toEqual({
      templateId: TEMPLATE_ID,
      cabinetId: 'cab-1',
      originDocumentId: 'origin-1',
      lifecycleStatus: DocumentLifecycleStatus.Ready,
      hasReviewReasons: true,
      fieldFilters: [{ name: 'amount', value: '100' }],
    });
  });

  it('drops documentTypeCode — the template is type-bound and the server narrows to its type', () => {
    const input = toExportDocumentsInput(TEMPLATE_ID, { documentTypeCode: 'invoice.general' });

    expect('documentTypeCode' in input).toBe(false);
  });

  it('leaves an unset filter unset rather than sending a filter for "none"', () => {
    const input = toExportDocumentsInput(TEMPLATE_ID, {});

    expect(input.hasReviewReasons).toBeUndefined();
    expect(input.originDocumentId).toBeUndefined();
    expect(input.lifecycleStatus).toBeUndefined();
    expect(input.cabinetId).toBeUndefined();
    expect(input.fieldFilters).toBeUndefined();
  });

  it('does not coerce hasReviewReasons=false into true', () => {
    // The toolbar only ever sends `true | undefined`, but nothing in the contract forbids false, and a
    // truthiness bug here would silently export the review queue instead of everything.
    expect(toExportDocumentsInput(TEMPLATE_ID, { hasReviewReasons: false }).hasReviewReasons).toBe(false);
  });
});

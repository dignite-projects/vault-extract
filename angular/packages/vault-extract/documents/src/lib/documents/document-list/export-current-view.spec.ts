import { describe, expect, it } from 'vitest';
import { DocumentLifecycleStatus, ExportFormat } from '@dignite/vault-extract';

import { DocumentListFilter, toExportDocumentsInput } from './export-current-view';

// #496 / #499: "download current view" is only honest if the export input carries every filter the list
// applied. These pin the two ways it could lie: dropping a filter the list applied (file wider than the
// screen), or inventing one the list did not (file narrower).

const TYPE = 'invoice.general';

describe('toExportDocumentsInput', () => {
  it('carries every filter the list applied', () => {
    const filter: DocumentListFilter = {
      documentTypeCode: TYPE,
      cabinetId: 'cab-1',
      originDocumentId: 'origin-1',
      lifecycleStatus: DocumentLifecycleStatus.Ready,
      hasReviewReasons: true,
      fieldFilters: [{ name: 'amount', value: '100' }],
    };

    expect(toExportDocumentsInput(TYPE, ExportFormat.Csv, filter)).toEqual({
      documentTypeCode: TYPE,
      format: ExportFormat.Csv,
      cabinetId: 'cab-1',
      originDocumentId: 'origin-1',
      lifecycleStatus: DocumentLifecycleStatus.Ready,
      hasReviewReasons: true,
      fieldFilters: [{ name: 'amount', value: '100' }],
    });
  });

  it('takes documentTypeCode from the caller, not from the list filter', () => {
    // #499: the export names its own type — the columns ARE that type's field definitions. The list filter's
    // copy is redundant; the caller passes the resolved code, so a stale filter entry cannot override it.
    const input = toExportDocumentsInput(TYPE, ExportFormat.Csv, { documentTypeCode: 'something.else' });

    expect(input.documentTypeCode).toBe(TYPE);
  });

  it('carries the requested format', () => {
    expect(toExportDocumentsInput(TYPE, ExportFormat.Xlsx, {}).format).toBe(ExportFormat.Xlsx);
  });

  it('leaves an unset filter unset rather than sending a filter for "none"', () => {
    const input = toExportDocumentsInput(TYPE, ExportFormat.Csv, {});

    expect(input.hasReviewReasons).toBeUndefined();
    expect(input.originDocumentId).toBeUndefined();
    expect(input.lifecycleStatus).toBeUndefined();
    expect(input.cabinetId).toBeUndefined();
    expect(input.fieldFilters).toBeUndefined();
  });

  it('does not coerce hasReviewReasons=false into true', () => {
    // The toolbar only ever sends `true | undefined`, but nothing in the contract forbids false, and a
    // truthiness bug here would silently export the review queue instead of everything.
    expect(
      toExportDocumentsInput(TYPE, ExportFormat.Csv, { hasReviewReasons: false }).hasReviewReasons,
    ).toBe(false);
  });
});

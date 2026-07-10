import { ExportDocumentsInput, ExportFormat, GetDocumentListInput } from '@dignite/vault-extract';

/**
 * #496: exactly the filter keys the document list composes in `buildFilter()`. Named so the round-trip
 * guard below has something to hold on to.
 */
export type DocumentListFilter = Pick<
  GetDocumentListInput,
  | 'documentTypeCode'
  | 'cabinetId'
  | 'originDocumentId'
  | 'lifecycleStatus'
  | 'hasReviewReasons'
  | 'fieldFilters'
>;

/**
 * Compile-time round-trip guard, half one of two. It asserts that every key of `DocumentListFilter` EXISTS on
 * `ExportDocumentsInput` — a list filter the export contract simply cannot carry breaks this line rather than
 * production (#496 — the bug that `hasReviewReasons` and `originDocumentId` used to be).
 *
 * On its own this is not enough: it says nothing about whether `toExportDocumentsInput` actually copies each
 * key's value, and TypeScript never complains about an optional property missing from an object literal. That
 * hole is closed by the spread in `toExportDocumentsInput` — see the note there. Guard + spread together make
 * a silently-dropped filter impossible; either one alone does not.
 *
 * #499 tightened the guard: while the export was template-bound, `documentTypeCode` was exempt (the template
 * supplied the type). The export names its own type now, so no key is exempt.
 */
type EveryListFilterRoundTrips = keyof DocumentListFilter extends keyof ExportDocumentsInput ? true : never;
export const LIST_FILTERS_ROUND_TRIP: EveryListFilterRoundTrips = true;

/**
 * Project the list's active filter onto the export contract, so the downloaded file is exactly the rows on
 * screen. `undefined` members stay `undefined` (an absent filter, not a filter for "none").
 *
 * Half two of the guard: every remaining filter key is forwarded by `...rest`, not by a hand-written list. A
 * filter added to `DocumentListFilter` therefore reaches the export with no edit here — where a hand-written
 * literal would have compiled happily while dropping it, because the target properties are optional.
 *
 * `documentTypeCode` is the one key the caller owns rather than the filter: the contract requires a non-empty
 * string, and the action is only offered once a single type is selected. Dropping the filter's copy explicitly
 * keeps the two from looking like rivals.
 */
export function toExportDocumentsInput(
  documentTypeCode: string,
  format: ExportFormat,
  filter: DocumentListFilter,
): ExportDocumentsInput {
  const { documentTypeCode: _suppliedByTheCaller, ...rest } = filter;

  return { documentTypeCode, format, ...rest };
}

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
 * Compile-time round-trip guard. "Download current view" is only honest if every filter the list can express
 * survives into the export; a list filter with no `ExportDocumentsInput` counterpart would silently widen
 * the exported set beyond what the operator is looking at (#496 — the bug that `hasReviewReasons` and
 * `originDocumentId` used to be). Adding such a filter breaks this line rather than production.
 *
 * #499 tightened it. While the export was template-bound, `documentTypeCode` was exempt (the template
 * supplied the type). The export now names the type itself, so there is no exempt key left: every member of
 * `DocumentListFilter` must exist on `ExportDocumentsInput`.
 */
type EveryListFilterRoundTrips = keyof DocumentListFilter extends keyof ExportDocumentsInput ? true : never;
export const LIST_FILTERS_ROUND_TRIP: EveryListFilterRoundTrips = true;

/**
 * Project the list's active filter onto the export contract, so the downloaded file is exactly the rows on
 * screen. `undefined` members stay `undefined` (an absent filter, not a filter for "none").
 *
 * `documentTypeCode` is passed separately because the contract requires it: extracted fields are type-scoped,
 * and the export's columns *are* that type's field definitions. The caller only offers the action once a
 * single type is selected, so it is never absent here.
 */
export function toExportDocumentsInput(
  documentTypeCode: string,
  format: ExportFormat,
  filter: DocumentListFilter,
): ExportDocumentsInput {
  return {
    documentTypeCode,
    format,
    lifecycleStatus: filter.lifecycleStatus,
    cabinetId: filter.cabinetId,
    originDocumentId: filter.originDocumentId,
    hasReviewReasons: filter.hasReviewReasons,
    fieldFilters: filter.fieldFilters,
  };
}

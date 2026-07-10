import { ExportDocumentsInput, GetDocumentListInput } from '@dignite/vault-extract';

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
 * The one list filter the export deliberately does not carry: a download config is type-bound
 * (`ExportTemplate.DocumentTypeId`) and the server always narrows the export to the template's own type,
 * so sending the type again would be redundant at best and contradictory at worst.
 */
type SuppliedByTheTemplate = 'documentTypeCode';

/**
 * Compile-time round-trip guard. "Export current view" is only honest if every filter the list can express
 * survives into the export; a list filter with no `ExportDocumentsInput` counterpart would silently widen
 * the exported set beyond what the operator is looking at (#496 — the bug that `hasReviewReasons` and
 * `originDocumentId` used to be). Adding such a filter breaks this line rather than production.
 */
type EveryListFilterRoundTrips =
  Exclude<keyof DocumentListFilter, SuppliedByTheTemplate> extends keyof ExportDocumentsInput ? true : never;
export const LIST_FILTERS_ROUND_TRIP: EveryListFilterRoundTrips = true;

/**
 * Project the list's active filter onto the export contract, so the downloaded file is exactly the rows on
 * screen. `undefined` members stay `undefined` (an absent filter, not a filter for "none").
 */
export function toExportDocumentsInput(
  templateId: string,
  filter: DocumentListFilter,
): ExportDocumentsInput {
  return {
    templateId,
    lifecycleStatus: filter.lifecycleStatus,
    cabinetId: filter.cabinetId,
    originDocumentId: filter.originDocumentId,
    hasReviewReasons: filter.hasReviewReasons,
    fieldFilters: filter.fieldFilters,
    // documentTypeCode is intentionally absent — see SuppliedByTheTemplate.
  };
}

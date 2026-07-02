import type { GetDocumentListInput } from '../proxy/documents/models';

// Flattens a GetDocumentListInput into a query-param map for the document list GET.
//
// The bug this fixes (#415): the generated DocumentService.getList passes `fieldFilters` — a
// DocumentFieldFilter[] (an array of OBJECTS) — straight into ABP's RestService params, which builds Angular
// HttpParams via `fromObject`. HttpParams serializes each array element with String(obj) => "[object Object]",
// so the GET sends `fieldFilters=%5Bobject%20Object%5D&...` and ASP.NET Core binds an EMPTY
// List<DocumentFieldFilter> — the field-value filter silently does nothing. (The backend unit tests call the
// app service directly, bypassing this HTTP serialization, so they never caught it.)
//
// The fix: expand `fieldFilters` into the indexed query notation ASP.NET Core's model binder reads —
// `fieldFilters[0].name=amount&fieldFilters[0].value=100&fieldFilters[1].min=A&...`. Scalar fields pass
// through unchanged (RestService then drops the undefined/empty/null ones as usual). Pure + exported so the
// serialization — the whole point of the fix — is unit-testable without the Angular runtime.
export function toDocumentListParams(input: GetDocumentListInput): Record<string, unknown> {
  const { fieldFilters, ...scalars } = input;
  const params: Record<string, unknown> = { ...scalars };

  (fieldFilters ?? []).forEach((filter, index) => {
    // Emit only the bounds actually present: a filter carries either `value` (equality) or one/both of
    // `min`/`max` (range), never a stray empty bound. `name` keys the filter to its FieldDefinition server-side.
    setIfPresent(params, `fieldFilters[${index}].name`, filter.name);
    setIfPresent(params, `fieldFilters[${index}].value`, filter.value);
    setIfPresent(params, `fieldFilters[${index}].min`, filter.min);
    setIfPresent(params, `fieldFilters[${index}].max`, filter.max);
  });

  return params;
}

// Set a key only when the value is meaningful. A literal "0" / "false" is a real value and MUST be kept
// (mirrors the composer's "0 is a real value" rule); only null / undefined / "" are dropped.
function setIfPresent(params: Record<string, unknown>, key: string, value: string | null | undefined): void {
  if (value !== null && value !== undefined && value !== '') {
    params[key] = value;
  }
}

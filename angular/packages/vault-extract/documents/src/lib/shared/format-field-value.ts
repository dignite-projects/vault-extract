/**
 * The separator a multi-value field's values are joined with for display.
 *
 * #501 item 6: this is the same separator the server writes into an exported CSV / XLSX cell
 * (`ExportCellRenderer.MultiValueSeparator`). The screen used to join with `", "` while the file joined with
 * `"; "`, so the same document's same field read `a, b, c` on screen and `a; b; c` in the file.
 *
 * The screen moved, not the file. A comma is the CSV delimiter: the writer would have to quote the cell, and a
 * consumer re-splitting a quoted cell on commas shreds one field across several columns. The file already has
 * consumers parsing it, and its bytes are the riskier thing to change.
 *
 * The constant cannot be shared across the language boundary. The two sides are held in step by naming the
 * literal on each: `format-field-value.spec.ts` here, `ExportCellRenderer_Tests` there.
 */
export const MULTI_VALUE_SEPARATOR = '; ';

/**
 * Renders an `ExtractedFields` value for display. Shared by the document list and detail views so
 * the two cannot drift (#212): multi-value fields arrive as JSON arrays, and a stray object must
 * never surface as `[object Object]`.
 * - null / undefined → "—"
 * - array → elements joined with `MULTI_VALUE_SEPARATOR` (empty array → "—")
 * - object → JSON
 * - scalar → String(value)
 */
export function formatExtractedFieldValue(value: unknown): string {
  if (value === null || value === undefined) return '—';
  if (Array.isArray(value)) {
    return value.length > 0 ? value.map(v => String(v)).join(MULTI_VALUE_SEPARATOR) : '—';
  }
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

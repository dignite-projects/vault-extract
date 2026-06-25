/**
 * Renders an `ExtractedFields` value for display. Shared by the document list and detail views so
 * the two cannot drift (#212): multi-value fields arrive as JSON arrays, and a stray object must
 * never surface as `[object Object]`.
 * - null / undefined → "—"
 * - array → elements joined with ", " (empty array → "—")
 * - object → JSON
 * - scalar → String(value)
 */
export function formatExtractedFieldValue(value: unknown): string {
  if (value === null || value === undefined) return '—';
  if (Array.isArray(value)) {
    return value.length > 0 ? value.map(v => String(v)).join(', ') : '—';
  }
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

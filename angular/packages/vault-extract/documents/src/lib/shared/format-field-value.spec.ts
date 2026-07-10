import { describe, expect, it } from 'vitest';

import { MULTI_VALUE_SEPARATOR, formatExtractedFieldValue } from './format-field-value';

// #501 item 6: the screen and the exported file must render a multi-value field identically. The server-side
// half of this pair is ExportCellRenderer.MultiValueSeparator, pinned by ExportCellRenderer_Tests. The constant
// cannot cross the language boundary, so each side names the literal and these two tests hold them in step.
describe('formatExtractedFieldValue', () => {
  it('joins a multi-value field with the same separator the exported file uses', () => {
    expect(formatExtractedFieldValue(['alpha', 'beta', 'gamma'])).toBe('alpha; beta; gamma');
  });

  it('pins the separator literal, which must equal ExportCellRenderer.MultiValueSeparator', () => {
    // Not a comma: a comma is the CSV delimiter, so the writer would quote the cell and a consumer re-splitting
    // it on commas would shred one field across several columns.
    expect(MULTI_VALUE_SEPARATOR).toBe('; ');
  });

  it('renders a single-element array without a trailing separator', () => {
    expect(formatExtractedFieldValue(['sole'])).toBe('sole');
  });

  it('renders an empty array as the em dash placeholder', () => {
    expect(formatExtractedFieldValue([])).toBe('—');
  });

  it('renders null and undefined as the em dash placeholder', () => {
    expect(formatExtractedFieldValue(null)).toBe('—');
    expect(formatExtractedFieldValue(undefined)).toBe('—');
  });

  it('renders scalars through String()', () => {
    expect(formatExtractedFieldValue('hello')).toBe('hello');
    expect(formatExtractedFieldValue(1000.5)).toBe('1000.5');
    expect(formatExtractedFieldValue(true)).toBe('true');
  });

  it('never lets an object surface as [object Object]', () => {
    expect(formatExtractedFieldValue({ a: 1 })).toBe('{"a":1}');
  });
});

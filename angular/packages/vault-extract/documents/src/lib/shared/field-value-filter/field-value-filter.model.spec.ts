import { describe, expect, it } from 'vitest';
import { FieldDataType } from '@dignite/vault-extract';

import { FilterRow, composeFieldFilters, rangeSupported } from './field-value-filter.model';

// #415: the field-value composer compiles editor rows into server-shaped DocumentFieldFilter values. These
// guard the two rules the backend enforces (ApplyFieldValueFilter): only Number/Date/DateTime may carry a
// range, and every emitted filter must have at least one of value/min/max — an incomplete filter would be
// an AbpValidationException, and a range on Text/Boolean a FieldTypeDoesNotSupportRange error.

// Full FilterRow with defaults so each test states only what it exercises.
function row(overrides: Partial<FilterRow>): FilterRow {
  return {
    key: 0,
    fieldName: 'amount',
    dataType: FieldDataType.Text,
    mode: 'eq',
    value: '',
    min: '',
    max: '',
    ...overrides,
  };
}

describe('rangeSupported', () => {
  it('is true only for Number / Date / DateTime', () => {
    expect(rangeSupported(FieldDataType.Number)).toBe(true);
    expect(rangeSupported(FieldDataType.Date)).toBe(true);
    expect(rangeSupported(FieldDataType.DateTime)).toBe(true);
  });

  it('is false for Text / Boolean / LongText', () => {
    expect(rangeSupported(FieldDataType.Text)).toBe(false);
    expect(rangeSupported(FieldDataType.Boolean)).toBe(false);
    expect(rangeSupported(FieldDataType.LongText)).toBe(false);
  });
});

describe('composeFieldFilters', () => {
  it('emits a Text equality as { name, value } (no min/max)', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'partyName', dataType: FieldDataType.Text, value: 'Acme' }),
    ]);
    expect(out).toEqual([{ name: 'partyName', value: 'Acme' }]);
  });

  it('emits a Boolean equality with the literal true/false string the server parses', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'signed', dataType: FieldDataType.Boolean, value: 'false' }),
    ]);
    expect(out).toEqual([{ name: 'signed', value: 'false' }]);
  });

  it('emits Number equality (eq mode) as a value, not a range', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'amount', dataType: FieldDataType.Number, mode: 'eq', value: '100' }),
    ]);
    expect(out).toEqual([{ name: 'amount', value: '100' }]);
  });

  it('keeps a literal "0" numeric equality (a falsy string must not be dropped)', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'amount', dataType: FieldDataType.Number, mode: 'eq', value: '0' }),
    ]);
    expect(out).toEqual([{ name: 'amount', value: '0' }]);
  });

  it('keeps "0" range bounds (a "0" bound is a real bound, not an unset one)', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'amount', dataType: FieldDataType.Number, mode: 'range', min: '0', max: '0' }),
    ]);
    expect(out).toEqual([{ name: 'amount', min: '0', max: '0' }]);
  });

  it('emits a two-sided Number range as { name, min, max } with no value', () => {
    const out = composeFieldFilters([
      row({ fieldName: 'amount', dataType: FieldDataType.Number, mode: 'range', min: '10', max: '20' }),
    ]);
    expect(out).toEqual([{ name: 'amount', min: '10', max: '20' }]);
  });

  it('emits a one-sided range (unset bound becomes null)', () => {
    expect(
      composeFieldFilters([
        row({ fieldName: 'd', dataType: FieldDataType.Date, mode: 'range', min: '2026-01-01' }),
      ]),
    ).toEqual([{ name: 'd', min: '2026-01-01', max: null }]);
    expect(
      composeFieldFilters([
        row({ fieldName: 'd', dataType: FieldDataType.Date, mode: 'range', max: '2026-12-31' }),
      ]),
    ).toEqual([{ name: 'd', min: null, max: '2026-12-31' }]);
  });

  it('drops a row with no field selected', () => {
    expect(composeFieldFilters([row({ fieldName: '', value: 'x' })])).toEqual([]);
  });

  it('drops an equality row whose value is blank (never sends an incomplete filter)', () => {
    expect(
      composeFieldFilters([row({ fieldName: 'amount', dataType: FieldDataType.Text, value: '   ' })]),
    ).toEqual([]);
  });

  it('drops a range row with neither bound', () => {
    expect(
      composeFieldFilters([
        row({ fieldName: 'amount', dataType: FieldDataType.Number, mode: 'range', min: ' ', max: '' }),
      ]),
    ).toEqual([]);
  });

  it('trims values and bounds', () => {
    expect(
      composeFieldFilters([row({ fieldName: 'a', dataType: FieldDataType.Text, value: '  hi  ' })]),
    ).toEqual([{ name: 'a', value: 'hi' }]);
    expect(
      composeFieldFilters([
        row({ fieldName: 'n', dataType: FieldDataType.Number, mode: 'range', min: ' 1 ', max: ' 9 ' }),
      ]),
    ).toEqual([{ name: 'n', min: '1', max: '9' }]);
  });

  it('never builds a range on a non-range-capable type — falls back to equality', () => {
    // The UI never offers range mode on Text, but even if a row arrives in range mode the compiler must
    // not emit a Text range (the server hard-errors it); it compiles the equality value instead.
    const out = composeFieldFilters([
      row({ fieldName: 't', dataType: FieldDataType.Text, mode: 'range', value: 'v', min: '1', max: '2' }),
    ]);
    expect(out).toEqual([{ name: 't', value: 'v' }]);
  });

  it('preserves order across multiple rows', () => {
    const out = composeFieldFilters([
      row({ key: 1, fieldName: 'a', dataType: FieldDataType.Text, value: 'x' }),
      row({ key: 2, fieldName: 'b', dataType: FieldDataType.Number, mode: 'range', min: '1', max: '2' }),
    ]);
    expect(out).toEqual([
      { name: 'a', value: 'x' },
      { name: 'b', min: '1', max: '2' },
    ]);
  });
});

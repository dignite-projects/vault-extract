import { describe, expect, it } from 'vitest';
import type { GetDocumentListInput } from '../proxy/documents/models';
import { toDocumentListParams } from './document-list-query.params';

// #415 fix: the document list GET must serialize fieldFilters (an array of objects) into the indexed query
// notation ASP.NET Core binds (fieldFilters[i].name / value / min / max). The generated proxy passed the raw
// array, which HttpParams stringifies to "[object Object]", so the backend bound NO filters and the feature
// silently did nothing. These lock the serialization; any regression reintroduces the silent no-op.

describe('toDocumentListParams', () => {
  it('expands an equality filter into indexed name + value keys, no bounds', () => {
    const params = toDocumentListParams({
      fieldFilters: [{ name: 'amount', value: '100' }],
    } as GetDocumentListInput);

    expect(params['fieldFilters[0].name']).toBe('amount');
    expect(params['fieldFilters[0].value']).toBe('100');
    expect(params['fieldFilters[0].min']).toBeUndefined();
    expect(params['fieldFilters[0].max']).toBeUndefined();
    // The raw array key that broke serialization must be gone.
    expect(params['fieldFilters']).toBeUndefined();
  });

  it('expands a two-sided range filter into indexed min + max keys, no value', () => {
    const params = toDocumentListParams({
      fieldFilters: [{ name: 'signedOn', min: '2024-01-01', max: '2024-12-31' }],
    } as GetDocumentListInput);

    expect(params['fieldFilters[0].name']).toBe('signedOn');
    expect(params['fieldFilters[0].min']).toBe('2024-01-01');
    expect(params['fieldFilters[0].max']).toBe('2024-12-31');
    expect(params['fieldFilters[0].value']).toBeUndefined();
  });

  it('emits only the present bound for a one-sided range', () => {
    const params = toDocumentListParams({
      fieldFilters: [{ name: 'amount', min: '50', max: null }],
    } as GetDocumentListInput);

    expect(params['fieldFilters[0].min']).toBe('50');
    expect(params['fieldFilters[0].max']).toBeUndefined();
  });

  it('indexes multiple filters independently', () => {
    const params = toDocumentListParams({
      fieldFilters: [
        { name: 'amount', value: '100' },
        { name: 'party', value: 'Acme' },
      ],
    } as GetDocumentListInput);

    expect(params['fieldFilters[0].name']).toBe('amount');
    expect(params['fieldFilters[1].name']).toBe('party');
    expect(params['fieldFilters[1].value']).toBe('Acme');
  });

  it('keeps a literal "0" value (a real value, not empty)', () => {
    const params = toDocumentListParams({
      fieldFilters: [{ name: 'balance', value: '0' }],
    } as GetDocumentListInput);

    expect(params['fieldFilters[0].value']).toBe('0');
  });

  it('passes scalar filters through and adds no fieldFilters keys when there are none', () => {
    const params = toDocumentListParams({
      documentTypeCode: 'contract.general',
      skipCount: 20,
      maxResultCount: 10,
    } as GetDocumentListInput);

    expect(params['documentTypeCode']).toBe('contract.general');
    expect(params['skipCount']).toBe(20);
    expect(params['maxResultCount']).toBe(10);
    expect(Object.keys(params).some(k => k.startsWith('fieldFilters'))).toBe(false);
  });
});

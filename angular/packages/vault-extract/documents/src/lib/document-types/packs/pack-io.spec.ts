import { describe, expect, it } from 'vitest';
import { DocumentTypePackDto } from '@dignite/vault-extract';

import {
  MAX_PACKS_PER_IMPORT,
  packFileName,
  parsePackFileText,
  serializePacks,
} from './pack-io';

// The uploaded file is the one import input the server cannot pre-shape, so the parser is where a bad file
// must fail with a clear reason instead of reaching the API as an opaque 400. These pin every branch.

const onePack: DocumentTypePackDto = {
  version: 1,
  typeCode: 'host.contract',
  displayName: 'Contract',
  fields: [{ name: 'amount', displayName: 'Amount' }],
};

describe('parsePackFileText', () => {
  it('accepts a single exported pack object and wraps it into an array', () => {
    const result = parsePackFileText(JSON.stringify(onePack));
    expect(result.error).toBeUndefined();
    expect(result.packs).toHaveLength(1);
    expect(result.packs?.[0].typeCode).toBe('host.contract');
  });

  it('accepts an export-all array as-is', () => {
    const result = parsePackFileText(
      JSON.stringify([onePack, { ...onePack, typeCode: 'host.invoice' }]),
    );
    expect(result.error).toBeUndefined();
    expect(result.packs).toHaveLength(2);
  });

  it('reports empty for blank input', () => {
    expect(parsePackFileText('   ').error).toBe('empty');
  });

  it('reports empty for an empty array', () => {
    expect(parsePackFileText('[]').error).toBe('empty');
  });

  it('reports invalid-json for non-JSON', () => {
    expect(parsePackFileText('not json {').error).toBe('invalid-json');
  });

  it('reports not-a-pack when an entry has no typeCode', () => {
    expect(parsePackFileText(JSON.stringify([{ displayName: 'x' }])).error).toBe('not-a-pack');
  });

  it('reports not-a-pack for a JSON scalar', () => {
    expect(parsePackFileText('42').error).toBe('not-a-pack');
  });

  it('reports not-a-pack when typeCode is blank', () => {
    expect(parsePackFileText(JSON.stringify([{ ...onePack, typeCode: '  ' }])).error).toBe(
      'not-a-pack',
    );
  });

  it('reports too-many past the import cap', () => {
    const many = Array.from({ length: MAX_PACKS_PER_IMPORT + 1 }, (_, i) => ({
      ...onePack,
      typeCode: `t${i}`,
    }));
    expect(parsePackFileText(JSON.stringify(many)).error).toBe('too-many');
  });

  it('accepts exactly the import cap', () => {
    const many = Array.from({ length: MAX_PACKS_PER_IMPORT }, (_, i) => ({
      ...onePack,
      typeCode: `t${i}`,
    }));
    expect(parsePackFileText(JSON.stringify(many)).packs).toHaveLength(MAX_PACKS_PER_IMPORT);
  });
});

describe('serializePacks / packFileName', () => {
  it('round-trips through parse', () => {
    const json = serializePacks([onePack]);
    expect(parsePackFileText(json).packs?.[0].typeCode).toBe('host.contract');
  });

  it('names the file with a zero-padded local timestamp', () => {
    // 2026-03-04 09:07:05 local time.
    const name = packFileName('document-types', new Date(2026, 2, 4, 9, 7, 5));
    expect(name).toBe('document-types-20260304-090705.json');
  });
});

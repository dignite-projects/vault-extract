import { describe, expect, it } from 'vitest';

import { exportFileName, parseAbpErrorMessage, readBlobErrorMessage } from './blob-download';

// #496: the export endpoint is responseType:'blob', so an over-limit failure arrives as a Blob body that
// ABP's global error interceptor cannot read. Without this parser the operator gets silence — the opposite
// of what the MaxExportDocumentCount fail-fast is for.

describe('parseAbpErrorMessage', () => {
  it('reads the message out of the ABP error envelope', () => {
    const body = JSON.stringify({
      error: { code: 'Extract:ExportDocumentLimitExceeded', message: 'The download matches 5001+ documents…' },
    });

    expect(parseAbpErrorMessage(body)).toBe('The download matches 5001+ documents…');
  });

  it('returns null for a non-ABP body so the caller can fall back to its own message', () => {
    expect(parseAbpErrorMessage('<html>502 Bad Gateway</html>')).toBeNull();
    expect(parseAbpErrorMessage('{"unrelated":true}')).toBeNull();
    expect(parseAbpErrorMessage('')).toBeNull();
  });

  it('treats an empty message as absent', () => {
    expect(parseAbpErrorMessage(JSON.stringify({ error: { message: '' } }))).toBeNull();
  });
});

describe('readBlobErrorMessage', () => {
  const envelope = JSON.stringify({ error: { message: 'Narrow the filter.' } });

  it('reads an ABP message via Blob.text() — the path every real browser takes', async () => {
    const blob = new Blob([envelope], { type: 'application/json' });
    // jsdom's Blob has no text(); browsers do. Graft it on so this test exercises the production path
    // rather than the fallback below.
    (blob as Blob & { text: () => Promise<string> }).text = () => Promise.resolve(envelope);

    expect(await readBlobErrorMessage({ error: blob })).toBe('Narrow the filter.');
  });

  it('falls back to FileReader where Blob.text() is missing (jsdom, Safari < 14)', async () => {
    const blob = new Blob([envelope], { type: 'application/json' });

    expect(await readBlobErrorMessage({ error: blob })).toBe('Narrow the filter.');
  });

  it('returns null when the body is not a Blob', async () => {
    expect(await readBlobErrorMessage({ error: { message: 'already parsed' } })).toBeNull();
    expect(await readBlobErrorMessage(new Error('network'))).toBeNull();
    expect(await readBlobErrorMessage(undefined)).toBeNull();
  });
});

describe('exportFileName', () => {
  it('names the file after the download config', () => {
    expect(exportFileName('invoices', false)).toBe('invoices.csv');
    expect(exportFileName('invoices', true)).toBe('invoices.xlsx');
  });
});

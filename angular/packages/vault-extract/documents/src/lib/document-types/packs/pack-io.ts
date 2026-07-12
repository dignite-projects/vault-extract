import { DocumentTypePackDto } from '@dignite/vault-extract';

/**
 * Config-pack import/export I/O helpers (#444 UI). Kept as pure functions — separate from the modal and the
 * list component — so the risky part (parsing an arbitrary uploaded file) is unit-tested in isolation, the
 * same split `export-current-view.ts` uses for the document-data download contract.
 */

/**
 * Client-side cap on packs per import, mirroring the server's `DocumentTypePackConsts.MaxPacksPerImport`.
 * The server stays the authority (it re-validates, and its const is the real compile-time ceiling); this only
 * turns an oversized file into a clear message instead of a round-trip that 400s.
 */
export const MAX_PACKS_PER_IMPORT = 100;

/** Pretty-print packs for download. Mirrors the two export shapes: one pack object, or an array of them. */
export function serializePacks(packs: DocumentTypePackDto | DocumentTypePackDto[]): string {
  return JSON.stringify(packs, null, 2);
}

/** `{label}-{yyyyMMdd-HHmmss}.json`, matching the local-time stamp shape of the document-data export (#499). */
export function packFileName(label: string, now: Date): string {
  const p = (n: number, width = 2) => String(n).padStart(width, '0');
  const stamp =
    `${p(now.getFullYear(), 4)}${p(now.getMonth() + 1)}${p(now.getDate())}` +
    `-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
  return `${label}-${stamp}.json`;
}

export type PackParseError = 'invalid-json' | 'empty' | 'not-a-pack' | 'too-many';

export interface PackParseResult {
  packs?: DocumentTypePackDto[];
  error?: PackParseError;
}

/**
 * Parse an uploaded pack file into a normalized `DocumentTypePackDto[]`. Accepts BOTH export shapes — a
 * single pack object (single-type export) and an array (export-all) — because either should re-import cleanly.
 *
 * Validation is deliberately shallow: it confirms only the top-level shape (each entry is an object carrying a
 * non-empty string `typeCode`), enough to reject an obviously-wrong file (a CSV, a document-data export,
 * unrelated JSON) with a local message. Per-field rules (name patterns, lengths, pack version) stay on the
 * server, the single source of truth — the SPA must not fork that contract.
 */
export function parsePackFileText(text: string): PackParseResult {
  if (!text.trim()) {
    return { error: 'empty' };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { error: 'invalid-json' };
  }

  const list = Array.isArray(parsed) ? parsed : [parsed];
  if (list.length === 0) {
    return { error: 'empty' };
  }
  if (list.length > MAX_PACKS_PER_IMPORT) {
    return { error: 'too-many' };
  }
  if (!list.every(isPackShaped)) {
    return { error: 'not-a-pack' };
  }

  return { packs: list as DocumentTypePackDto[] };
}

function isPackShaped(value: unknown): value is DocumentTypePackDto {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const typeCode = (value as { typeCode?: unknown }).typeCode;
  return typeof typeCode === 'string' && typeCode.trim().length > 0;
}

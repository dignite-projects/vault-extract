/**
 * One-shot blob download + the error path that comes with it.
 *
 * Distinct from {@link DocumentFileBlobService}, which owns a *cached* preview blob whose object URL
 * outlives the click and is revoked on component destroy. Here the blob is fetched, handed to the browser,
 * and forgotten.
 *
 * The error half exists because the export endpoint is declared `responseType: 'blob'`. On a non-2xx
 * response Angular still hands the body back as a Blob, so ABP's global error interceptor cannot read the
 * `{ error: { code, message } }` envelope inside it and the operator sees nothing — which is exactly the
 * wrong outcome for the over-limit fail-fast (`Extract:ExportDocumentLimitExceeded`), whose whole purpose
 * is to tell the operator to narrow the filter rather than silently hand them a truncated file.
 */

/** `<config name>.csv` / `.xlsx` — the single place both download surfaces name the exported file. */
export function exportFileName(templateName: string, isXlsx: boolean): string {
  return templateName + (isXlsx ? '.xlsx' : '.csv');
}

/** Trigger a browser download for a freshly fetched blob. */
export function triggerBlobDownload(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  // Defer revoke so the browser's download handler has taken ownership of the blob before its backing
  // URL is released (avoids a race on large files).
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

/**
 * Pull the human-readable message out of ABP's error envelope. Returns null when the body is not an ABP
 * error (opaque 500, HTML error page, empty body), so callers fall back to their own generic message
 * instead of surfacing a parser artifact.
 */
export function parseAbpErrorMessage(body: string): string | null {
  if (!body) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return null;
  }

  const message = (parsed as { error?: { message?: unknown } })?.error?.message;
  return typeof message === 'string' && message.length > 0 ? message : null;
}

/**
 * Read a Blob as text. Prefers `Blob.text()` (every browser Angular targets) and falls back to `FileReader`
 * where it is missing — jsdom, and Safari before 14. Without the fallback this module would work in
 * production and return null under test, which is precisely backwards for the one code path whose entire
 * job is to keep a failure from being silent.
 */
function readBlobAsText(blob: Blob): Promise<string> {
  if (typeof blob.text === 'function') {
    return blob.text();
  }

  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result ?? ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
}

/**
 * Read an ABP error message out of an HttpErrorResponse whose body arrived as a Blob. Never rejects:
 * an unreadable body simply yields null.
 */
export async function readBlobErrorMessage(error: unknown): Promise<string | null> {
  const body = (error as { error?: unknown })?.error;
  if (!(body instanceof Blob)) return null;

  try {
    return parseAbpErrorMessage(await readBlobAsText(body));
  } catch {
    return null;
  }
}

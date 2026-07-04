// Client-side upload constraints — MUST mirror the backend DocumentConsts
// (core/src/Dignite.Vault.Extract.Domain.Shared/Documents/DocumentConsts.cs:
//  MaxUploadFileBytes / AllowedUploadContentTypesByExtension, #221 / #471).
// These are a UX nicety (instant feedback + file-picker filtering); the backend
// is the authoritative fail-closed gate. If the backend defaults change, change
// these to match — single source of truth on the Angular side lives here.

/** Maximum upload size in bytes (20 MiB). Mirrors DocumentConsts.MaxUploadFileBytes. */
export const MAX_UPLOAD_FILE_BYTES = 20 * 1024 * 1024;

/** Exact extension/MIME pairs. Mirrors DocumentConsts.AllowedUploadContentTypesByExtension. */
export const ALLOWED_UPLOAD_TYPES_BY_EXTENSION = {
  '.jpg': ['image/jpeg'],
  '.jpeg': ['image/jpeg'],
  '.png': ['image/png'],
  '.gif': ['image/gif'],
  '.webp': ['image/webp'],
  '.pdf': ['application/pdf'],
  '.csv': ['text/csv', 'application/csv', 'application/vnd.ms-excel'],
  '.tsv': ['text/tab-separated-values', 'text/tsv'],
  '.txt': ['text/plain'],
  '.docx': ['application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
  '.pptx': ['application/vnd.openxmlformats-officedocument.presentationml.presentation'],
  '.xlsx': ['application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
} as const satisfies Readonly<Record<string, readonly string[]>>;

export const ALLOWED_UPLOAD_EXTENSIONS = Object.keys(ALLOWED_UPLOAD_TYPES_BY_EXTENSION);
export const ALLOWED_UPLOAD_CONTENT_TYPES = [
  ...new Set(
    Object.values(ALLOWED_UPLOAD_TYPES_BY_EXTENSION).reduce<string[]>(
      (all, contentTypes) => [...all, ...contentTypes],
      [],
    ),
  ),
];

/**
 * Value for a file input's `accept` attribute, derived from the whitelists so the
 * native picker filters to exactly the allowed set (no `image/*` over-reach).
 */
export const UPLOAD_ACCEPT_ATTRIBUTE = [
  ...ALLOWED_UPLOAD_CONTENT_TYPES,
  ...ALLOWED_UPLOAD_EXTENSIONS,
].join(',');

/** True only when the extension and MIME type form an approved pair. */
export function isAllowedUpload(file: File): boolean {
  const dot = file.name.lastIndexOf('.');
  const ext = dot >= 0 ? file.name.slice(dot).toLowerCase() : '';
  const allowedTypes = ALLOWED_UPLOAD_TYPES_BY_EXTENSION[
    ext as keyof typeof ALLOWED_UPLOAD_TYPES_BY_EXTENSION
  ] as readonly string[] | undefined;
  return allowedTypes?.includes(file.type) ?? false;
}

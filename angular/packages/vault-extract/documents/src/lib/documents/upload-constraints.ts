// Client-side upload constraints — MUST mirror the backend DocumentConsts
// (core/src/Dignite.Vault.Extract.Domain.Shared/Documents/DocumentConsts.cs:
//  MaxUploadFileBytes / AllowedUploadContentTypes / AllowedUploadExtensions, #221).
// These are a UX nicety (instant feedback + file-picker filtering); the backend
// is the authoritative fail-closed gate. If the backend defaults change, change
// these to match — single source of truth on the Angular side lives here.

/** Maximum upload size in bytes (20 MiB). Mirrors DocumentConsts.MaxUploadFileBytes. */
export const MAX_UPLOAD_FILE_BYTES = 20 * 1024 * 1024;

/** Allowed MIME types. Mirrors DocumentConsts.AllowedUploadContentTypes. */
export const ALLOWED_UPLOAD_CONTENT_TYPES = [
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
  'application/pdf',
] as const;

/** Allowed file extensions (lowercase, leading dot). Mirrors DocumentConsts.AllowedUploadExtensions. */
export const ALLOWED_UPLOAD_EXTENSIONS = [
  '.jpg',
  '.jpeg',
  '.png',
  '.gif',
  '.webp',
  '.pdf',
] as const;

/**
 * Value for a file input's `accept` attribute, derived from the whitelists so the
 * native picker filters to exactly the allowed set (no `image/*` over-reach).
 */
export const UPLOAD_ACCEPT_ATTRIBUTE = [
  ...ALLOWED_UPLOAD_CONTENT_TYPES,
  ...ALLOWED_UPLOAD_EXTENSIONS,
].join(',');

/** True when both the MIME type and the extension are in their respective whitelists (mirrors the backend dual check). */
export function isAllowedUpload(file: File): boolean {
  const typeOk = (ALLOWED_UPLOAD_CONTENT_TYPES as readonly string[]).includes(file.type);
  const dot = file.name.lastIndexOf('.');
  const ext = dot >= 0 ? file.name.slice(dot).toLowerCase() : '';
  const extOk = (ALLOWED_UPLOAD_EXTENSIONS as readonly string[]).includes(ext);
  return typeOk && extOk;
}

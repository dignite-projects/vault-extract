/**
 * Content-type predicates for the file preview path. Shared by the document detail Tab and the
 * standalone file-preview page (#277) so the two cannot drift on which content types render inline.
 * Kept as pure functions (not signals) — each component still wraps them in its own computed over
 * its own `document()` signal, but the classification rule itself lives in one place.
 */
export function isImageContentType(contentType: string | null | undefined): boolean {
  return !!contentType && contentType.startsWith('image/');
}

export function isPdfContentType(contentType: string | null | undefined): boolean {
  return contentType === 'application/pdf';
}

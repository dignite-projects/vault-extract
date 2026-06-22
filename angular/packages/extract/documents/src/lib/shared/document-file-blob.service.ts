import { DestroyRef, Injectable, OnDestroy, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { DocumentService } from '@dignite/extract';

/**
 * Owns the original-file blob lifecycle for the document preview path (#277): fetch via
 * DocumentService.getBlob (Bearer token, never in the URL) → createObjectURL → sanitize for the
 * PDF iframe → revoke on destroy. Both the detail page's Original File tab and the standalone
 * file-preview page consume this single seam so the lifecycle can no longer drift across two
 * hand-copied implementations.
 *
 * NOT `providedIn: 'root'` — declared in each consuming component's `providers`, so one instance
 * lives per component and its `ngOnDestroy` revoke is scoped to that component's lifetime.
 */
@Injectable()
export class DocumentFileBlobService implements OnDestroy {
  private readonly documentService = inject(DocumentService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly destroyRef = inject(DestroyRef);

  // raw blob: URL — used for <img> src, download anchors/trigger, and ngOnDestroy revoke.
  readonly blobUrl = signal<string | null>(null);
  // PDF iframe resource URL must pass through DomSanitizer or Angular drops it as unsafe
  // (self-minted same-origin blob:, safe to trust).
  readonly safeResourceUrl = signal<SafeResourceUrl | null>(null);
  readonly isLoading = signal(false);
  // Unified failure signal: blob download failed, or an <img>/iframe render failed (markError).
  readonly hasError = signal(false);

  /**
   * Lazy-load the blob once. Idempotent: short-circuits when already cached or a request is in
   * flight, so it is safe to call from a Tab activation, a Refresh, or a download click without
   * duplicate fetches. Observe outcome via the `blobUrl` / `isLoading` / `hasError` signals.
   */
  ensureLoaded(documentId: string): void {
    if (this.blobUrl() || this.isLoading()) return;
    this.isLoading.set(true);
    this.hasError.set(false);

    this.documentService
      .getBlob(documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const url = URL.createObjectURL(blob);
          this.blobUrl.set(url);
          this.safeResourceUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
          this.isLoading.set(false);
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  // Clear the error so an <img>/iframe rebuild retries; keeps any cached blob.
  resetError(): void {
    this.hasError.set(false);
  }

  // Drop the cached blob entirely and return to the pristine state. Used when the host component is reused
  // for a different document (the detail page reacts to :id route changes instead of being re-created), so
  // the previous document's blob never leaks into the new one's preview. Revokes the object URL — a fresh
  // ensureLoaded() will fetch and mint a new one.
  reset(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
    this.blobUrl.set(null);
    this.safeResourceUrl.set(null);
    this.isLoading.set(false);
    this.hasError.set(false);
  }

  // Flag a render failure (e.g. <img> decode error after the blob downloaded fine).
  markError(): void {
    this.hasError.set(true);
  }

  // Trigger a browser download from the cached blob via a hidden <a download>. Does not revoke —
  // the same URL may still back an inline preview; ngOnDestroy owns the single revoke.
  download(fileName: string): void {
    const url = this.blobUrl();
    if (!url) return;
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
  }

  ngOnDestroy(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { LocalizationPipe } from '@abp/ng.core';
import { DocumentDto, DocumentService } from '@dignite/vault-extract';
import { DocumentFileBlobService } from '../../shared/document-file-blob.service';
import { isImageContentType, isPdfContentType } from '../../shared/content-type';

// File preview page (route documents/:id/file). Replaces the old detail-page openFile() blob new-tab
// shortcut with a readable /documents/{id}/file URL that includes the document ID. The file content is
// still fetched through DocumentService.getBlob with a Bearer token and embedded as a blob, so the token
// never enters the URL. Blob lifecycle is centralized in DocumentFileBlobService (#277).
@Component({
  selector: 'lib-document-file-preview',
  templateUrl: './document-file-preview.component.html',
  imports: [CommonModule, LocalizationPipe],
  providers: [DocumentFileBlobService],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentFilePreviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly fileBlob = inject(DocumentFileBlobService);

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  hasError = signal(false);

  private documentId!: string;

  readonly fileName = computed(
    () => this.document()?.fileOrigin?.originalFileName || this.document()?.title || '',
  );
  readonly contentType = computed(() => this.document()?.fileOrigin?.contentType ?? '');
  readonly isImage = computed(() => isImageContentType(this.contentType()));
  readonly isPdf = computed(() => isPdfContentType(this.contentType()));
  // Sub-documents (#306/#346) carry no FileOrigin — there is no original file to fetch. Gate the blob fetch
  // and the viewer on this so the page never calls GetBlobAsync for a blob-less document
  // (Extract:DocumentNoSourceBlob); the template shows a neutral notice instead.
  readonly hasSourceFile = computed(() => !!this.document()?.fileOrigin);

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.hasError.set(false);
    // Fetch metadata first to get contentType and filename for rendering decisions, then let the service
    // fetch the blob body.
    this.documentService
      .get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: doc => {
          this.document.set(doc);
          this.isLoading.set(false);
          // A sub-document has no source blob; skip the fetch (would throw Extract:DocumentNoSourceBlob).
          if (doc.fileOrigin) {
            this.fileBlob.ensureLoaded(this.documentId);
          }
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  back(): void {
    this.router.navigate(['/documents', this.documentId]);
  }
}

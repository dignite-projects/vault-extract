import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnInit, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { CabinetDto, CabinetService, DocumentUploadService, EXTRACT_PERMISSIONS } from '@dignite/vault-extract';
import { from, of } from 'rxjs';
import { catchError, map, mergeMap } from 'rxjs/operators';
import {
  MAX_UPLOAD_FILE_BYTES,
  UPLOAD_ACCEPT_ATTRIBUTE,
  isAllowedUpload,
} from '../upload-constraints';

// Limits the number of concurrent /api/documents/upload requests to avoid
// exhausting the browser's per-origin connection pool and overloading the
// server when the user drops dozens of files at once.
const MAX_CONCURRENT_UPLOADS = 3;

interface FileUploadState {
  key: string;
  name: string;
  done: boolean;
  error: boolean;
  documentId?: string;
  errorMessage?: string;
}

@Component({
  selector: 'lib-document-upload',
  templateUrl: './document-upload.component.html',
  styleUrls: ['./document-upload.component.scss'],
  imports: [CommonModule, FormsModule, LocalizationPipe, RouterModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentUploadComponent implements OnInit {
  private readonly documentUploadService = inject(DocumentUploadService);
  private readonly cabinetService = inject(CabinetService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  // Primary file-picker trigger; used to restore focus after the result queue is
  // cleared, because the button the user just clicked is removed from the DOM.
  private readonly browseButton = viewChild<ElementRef<HTMLButtonElement>>('browseButton');

  // Cabinet selection requires Cabinets.Default permission because backend getList is [Authorize].
  // Without permission, hide the dropdown and upload as unclassified.
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Default,
  );
  cabinets = signal<CabinetDto[]>([]);
  selectedCabinetId = signal<string>('');

  // Picker `accept` filter, derived from the shared whitelist (mirrors backend, #221).
  readonly acceptAttribute = UPLOAD_ACCEPT_ATTRIBUTE;

  isDragOver = signal(false);
  isUploading = signal(false);
  uploadingFiles = signal<FileUploadState[]>([]);

  readonly hasUploadResults = computed(
    () =>
      this.uploadingFiles().length > 0 &&
      !this.isUploading() &&
      this.uploadingFiles().every(file => file.done || file.error),
  );
  readonly hasUploadErrors = computed(
    () => this.hasUploadResults() && this.uploadingFiles().some(file => file.error),
  );
  readonly successfulUploadCount = computed(
    () => this.uploadingFiles().filter(file => file.done).length,
  );
  readonly canAcceptFiles = computed(() => !this.isUploading() && !this.hasUploadResults());

  ngOnInit(): void {
    if (this.canViewCabinets) {
      this.cabinetService.getList()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: list => this.cabinets.set(list),
          error: () => this.cabinets.set([]),
        });
    }
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.canAcceptFiles()) return;

    this.isDragOver.set(true);
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver.set(false);
    if (!this.canAcceptFiles()) return;

    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.uploadFiles(Array.from(files));
    }
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.uploadFiles(Array.from(input.files));
      input.value = '';
    }
  }

  openFilePicker(input: HTMLInputElement): void {
    if (!this.canAcceptFiles()) return;

    input.click();
  }

  resetQueue(): void {
    this.uploadingFiles.set([]);
    this.isDragOver.set(false);
    // Restore focus to the primary trigger once the empty state re-renders;
    // the button the user just clicked is removed when the queue is cleared.
    afterNextRender(() => this.browseButton()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }

  private uploadFiles(files: File[]): void {
    if (!this.canAcceptFiles()) return;

    // Mirror the backend fail-closed gate (#221): MIME + extension whitelist, then size.
    const valid = files.filter(f => {
      if (!isAllowedUpload(f)) {
        this.toaster.error('::Document:UnsupportedFileType', '::Error');
        return false;
      }
      if (f.size > MAX_UPLOAD_FILE_BYTES) {
        this.toaster.error('::Document:FileTooLarge', '::Error');
        return false;
      }
      return true;
    });

    if (valid.length === 0) return;

    this.isUploading.set(true);
    this.uploadingFiles.set(
      valid.map((file, index) => ({
        key: `${file.name}-${file.lastModified}-${file.size}-${index}`,
        name: file.name,
        done: false,
        error: false,
      })),
    );

    const indexed = valid.map((file, idx) => ({ file, idx }));
    from(indexed)
      .pipe(
        mergeMap(
          ({ file, idx }) =>
            this.documentUploadService.upload(file, this.selectedCabinetId() || undefined).pipe(
              map(document => ({
                idx,
                success: true,
                documentId: document.id,
                errorMessage: undefined as string | undefined,
              })),
              catchError(err => {
                const errorMessage: string | undefined = err?.error?.error?.message;
                return of({ idx, success: false, documentId: undefined, errorMessage });
              }),
            ),
          MAX_CONCURRENT_UPLOADS,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ idx, success, documentId, errorMessage }) => {
          this.uploadingFiles.update(list =>
            list.map((item, j) =>
              j === idx
                ? {
                    ...item,
                    done: success,
                    error: !success,
                    documentId: success ? documentId : undefined,
                    errorMessage,
                  }
                : item,
            ),
          );
        },
        complete: () => {
          this.isUploading.set(false);
          const hasError = this.uploadingFiles().some(f => f.error);
          if (!hasError) {
            this.toaster.success('::Document:UploadedSuccessfully', '::Success');
          }
        },
      });
  }
}

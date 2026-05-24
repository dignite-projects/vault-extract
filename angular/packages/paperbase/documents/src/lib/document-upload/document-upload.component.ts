import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { CabinetDto, CabinetService, DocumentService, PAPERBASE_PERMISSIONS } from '@dignite/paperbase';
import { from, of } from 'rxjs';
import { catchError, map, mergeMap } from 'rxjs/operators';

// Limits the number of concurrent /api/documents/upload requests to avoid
// exhausting the browser's per-origin connection pool and overloading the
// server when the user drops dozens of files at once.
const MAX_CONCURRENT_UPLOADS = 3;

interface FileUploadState {
  name: string;
  done: boolean;
  error: boolean;
  errorMessage?: string;
}

@Component({
  selector: 'lib-document-upload',
  templateUrl: './document-upload.component.html',
  styleUrls: ['./document-upload.component.scss'],
  imports: [CommonModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentUploadComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly cabinetService = inject(CabinetService);
  private readonly router = inject(Router);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  // 选柜需要 Cabinets.Default 权限（getList 后端 [Authorize]）；无权限则不显示下拉，上传为未归类。
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Cabinets.Default,
  );
  cabinets = signal<CabinetDto[]>([]);
  selectedCabinetId = signal<string>('');

  isDragOver = signal(false);
  isUploading = signal(false);
  hasDoneWithErrors = signal(false);
  uploadingFiles = signal<FileUploadState[]>([]);

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

  private uploadFiles(files: File[]): void {
    const allowedTypes = ['image/jpeg', 'image/png', 'image/gif', 'image/webp', 'application/pdf'];
    const maxSizeBytes = 20 * 1024 * 1024;

    const valid = files.filter(f => {
      if (!allowedTypes.includes(f.type)) {
        this.toaster.error('::Document:UnsupportedFileType', '::Error');
        return false;
      }
      if (f.size > maxSizeBytes) {
        this.toaster.error('::Document:FileTooLarge', '::Error');
        return false;
      }
      return true;
    });

    if (valid.length === 0) return;

    this.isUploading.set(true);
    this.hasDoneWithErrors.set(false);
    this.uploadingFiles.set(valid.map(f => ({ name: f.name, done: false, error: false })));

    const indexed = valid.map((file, idx) => ({ file, idx }));
    from(indexed)
      .pipe(
        mergeMap(
          ({ file, idx }) =>
            this.documentService.upload(file, this.selectedCabinetId() || undefined).pipe(
              map(() => ({ idx, success: true, errorMessage: undefined as string | undefined })),
              catchError(err => {
                const errorMessage: string | undefined = err?.error?.error?.message;
                return of({ idx, success: false, errorMessage });
              }),
            ),
          MAX_CONCURRENT_UPLOADS,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ idx, success, errorMessage }) => {
          this.uploadingFiles.update(list =>
            list.map((item, j) =>
              j === idx ? { ...item, done: success, error: !success, errorMessage } : item,
            ),
          );
        },
        complete: () => {
          this.isUploading.set(false);
          const hasError = this.uploadingFiles().some(f => f.error);
          if (hasError) {
            this.hasDoneWithErrors.set(true);
          } else {
            this.toaster.success('::Document:UploadedSuccessfully', '::Success');
            this.router.navigate(['/documents']);
          }
        },
      });
  }

  continueToDocuments(): void {
    this.router.navigate(['/documents']);
  }
}

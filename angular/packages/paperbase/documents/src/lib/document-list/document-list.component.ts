import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
  computed,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Confirmation } from '@abp/ng.theme.shared';
import {
  CabinetDto,
  CabinetService,
  DocumentLifecycleStatus,
  DocumentListItemDto,
  DocumentReviewStatus,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  GetDocumentListInput,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';
import { from, of } from 'rxjs';
import { catchError, map, mergeMap } from 'rxjs/operators';

// Mirrors document-upload.component.ts. Limits concurrent /upload requests
// so a 50-file drop does not saturate the browser connection pool.
const MAX_CONCURRENT_UPLOADS = 3;

interface UploadResult {
  fileName: string;
  documentId?: string;
  succeeded: boolean;
  errorMessage?: string;
}

@Component({
  selector: 'lib-document-list',
  templateUrl: './document-list.component.html',
  styleUrls: ['./document-list.component.scss'],
  imports: [CommonModule, RouterModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentListComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly cabinetService = inject(CabinetService);
  private readonly router = inject(Router);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );
  readonly canConfirm = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Cabinets.Default,
  );

  documents = signal<PagedResultDto<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  isExporting = signal(false);
  isBulkUploading = signal(false);
  bulkUploadResults = signal<UploadResult[]>([]);

  reviewStatusFilter = signal<DocumentReviewStatus | undefined>(undefined);
  typeFilter = signal<string>('');
  cabinetFilter = signal<string>('');
  lifecycleFilter = signal<DocumentLifecycleStatus | undefined>(undefined);
  keyword = signal<string>('');
  confirmingDoc = signal<DocumentListItemDto | null>(null);
  documentTypes = signal<DocumentTypeDto[]>([]);
  cabinets = signal<CabinetDto[]>([]);
  selectedTypeCode = signal('');
  isConfirming = signal(false);

  page = signal(0);
  pageSize = 10;
  totalPages = computed(() => Math.ceil(this.documents().totalCount / this.pageSize));
  paginationPages = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));
  pendingReviewCount = computed(() =>
    this.documents().items.filter(d => d.reviewStatus === DocumentReviewStatus.PendingReview).length
  );

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;

  ngOnInit(): void {
    this.loadList();
    // GetVisible is gated by the ConfirmClassification permission; only fetch when
    // the operator can actually act on the confirm dialog (avoids a 403 on load
    // for view-only users).
    if (this.canConfirm) {
      this.loadDocumentTypes();
    }
    // Cabinet getList is gated by Cabinets.Default; only fetch when granted to
    // avoid a 403 for users without cabinet access (cabinet filter/labels hidden).
    if (this.canViewCabinets) {
      this.loadCabinets();
    }
  }

  refresh(): void {
    this.loadList();
  }

  onLifecycleFilterChange(value: DocumentLifecycleStatus | undefined): void {
    this.lifecycleFilter.set(value);
    this.page.set(0);
    this.loadList();
  }

  onTypeFilterChange(value: string): void {
    this.typeFilter.set(value);
    this.page.set(0);
    this.loadList();
  }

  onCabinetFilterChange(value: string): void {
    this.cabinetFilter.set(value);
    this.page.set(0);
    this.loadList();
  }

  applyKeyword(): void {
    this.page.set(0);
    this.loadList();
  }

  clearKeyword(): void {
    if (!this.keyword()) return;
    this.keyword.set('');
    this.page.set(0);
    this.loadList();
  }

  private buildFilter(): GetDocumentListInput {
    return {
      documentTypeCode: this.typeFilter() || undefined,
      cabinetId: this.cabinetFilter() || undefined,
      lifecycleStatus: this.lifecycleFilter(),
      reviewStatus: this.reviewStatusFilter(),
      keyword: this.keyword().trim() || undefined,
    };
  }

  // Visible document types for the current layer (Host admin → Host types;
  // tenant admin → that tenant's types). Drives the confirm-classification picker.
  private loadDocumentTypes(): void {
    this.documentTypeService.getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => this.documentTypes.set(types),
        error: () => this.documentTypes.set([]),
      });
  }

  // Visible cabinets for the current layer — drives the cabinet filter and the
  // cabinet-name label column (list DTO carries only cabinetId; we map id → name).
  private loadCabinets(): void {
    this.cabinetService.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => this.cabinets.set(list),
        error: () => this.cabinets.set([]),
      });
  }

  cabinetName(doc: DocumentListItemDto): string | null {
    if (!doc.cabinetId) return null;
    return this.cabinets().find(c => c.id === doc.cabinetId)?.displayName ?? null;
  }

  private loadList(): void {
    this.isLoading.set(true);
    this.documentService.getList({
      ...this.buildFilter(),
      maxResultCount: this.pageSize,
      skipCount: this.page() * this.pageSize,
      sorting: 'creationTime desc',
    })
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe({
      next: result => {
        this.documents.set(result);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  navigateTo(page: number): void {
    this.page.set(page);
    this.loadList();
  }

  openDetail(doc: DocumentListItemDto): void {
    this.router.navigate(['/documents', doc.id]);
  }

  uploadNew(): void {
    this.router.navigate(['/documents/upload']);
  }

  onBulkFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files?.length) return;
    const files = Array.from(input.files);

    this.isBulkUploading.set(true);
    this.bulkUploadResults.set([]);

    from(files)
      .pipe(
        mergeMap(
          file =>
            this.documentService.upload(file).pipe(
              map(doc => ({ fileName: file.name, documentId: doc.id, succeeded: true } as UploadResult)),
              catchError(err =>
                of({ fileName: file.name, succeeded: false, errorMessage: err?.message } as UploadResult),
              ),
            ),
          MAX_CONCURRENT_UPLOADS,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: result => this.bulkUploadResults.update(r => [...r, result]),
        complete: () => {
          this.isBulkUploading.set(false);
          this.loadList();
          input.value = '';
        },
      });
  }

  exportCsv(): void {
    const url = this.documentService.getExportUrl(this.buildFilter());
    window.open(url, '_blank');
  }

  delete(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status === Confirmation.Status.confirm) {
          this.documentService.delete(doc.id)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
            next: () => {
              this.toaster.success('::Document:DeletedSuccessfully', '::Success');
              this.loadList();
            },
          });
        }
      });
  }

  toggleManualReviewFilter(): void {
    this.reviewStatusFilter.update(v =>
      v === DocumentReviewStatus.PendingReview ? undefined : DocumentReviewStatus.PendingReview
    );
    this.page.set(0);
    this.loadList();
  }

  needsConfirmation(doc: DocumentListItemDto): boolean {
    return doc.reviewStatus === DocumentReviewStatus.PendingReview;
  }

  isProcessingDocument(doc: DocumentListItemDto): boolean {
    return doc.reviewStatus !== DocumentReviewStatus.PendingReview &&
      (doc.lifecycleStatus === DocumentLifecycleStatus.Processing ||
       doc.lifecycleStatus === DocumentLifecycleStatus.Uploaded);
  }

  openConfirmDialog(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.confirmingDoc.set(doc);
    // Pre-select the document's current (low-confidence) classification when present,
    // so the operator usually just confirms; otherwise force an explicit choice.
    this.selectedTypeCode.set(doc.documentTypeCode ?? '');
  }

  closeConfirmDialog(): void {
    this.confirmingDoc.set(null);
    this.selectedTypeCode.set('');
  }

  submitConfirmation(): void {
    const doc = this.confirmingDoc();
    if (!doc || !this.selectedTypeCode()) return;
    this.isConfirming.set(true);
    this.documentService.confirmClassification(doc.id, this.selectedTypeCode())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: () => {
        this.isConfirming.set(false);
        this.closeConfirmDialog();
        this.toaster.success('::Document:ClassificationConfirmed', '::Success');
        this.loadList();
      },
      error: () => {
        this.isConfirming.set(false);
        this.toaster.error('::Document:ConfirmFailed', '::Error');
      },
    });
  }

  getStatusBadgeClass(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:
        return 'badge bg-secondary';
      case DocumentLifecycleStatus.Processing:
        return 'badge bg-warning text-dark';
      case DocumentLifecycleStatus.Ready:
        return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:
        return 'badge bg-danger';
      default:
        return 'badge bg-secondary';
    }
  }

  getDocumentStatusBadgeClass(doc: DocumentListItemDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return 'badge bg-warning text-dark';
    }

    return this.getStatusBadgeClass(doc.lifecycleStatus);
  }

  getStatusLabel(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:
        return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing:
        return '::Document:Status:Processing';
      case DocumentLifecycleStatus.Ready:
        return '::Document:Status:Ready';
      case DocumentLifecycleStatus.Failed:
        return '::Document:Status:Failed';
      default:
        return '::Document:Status:Unknown';
    }
  }

  getDocumentStatusLabel(doc: DocumentListItemDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return '::DocumentReviewStatus:PendingReview';
    }

    return this.getStatusLabel(doc.lifecycleStatus);
  }

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }

  formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}

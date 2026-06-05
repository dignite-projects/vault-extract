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
  FieldDefinitionDto,
  FieldDefinitionService,
  GetDocumentListInput,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';
import { formatExtractedFieldValue } from '../../shared/format-field-value';

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
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly cabinetService = inject(CabinetService);
  private readonly router = inject(Router);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  // Shared with the detail view so multi-value (#212) / object cells render consistently.
  protected readonly formatFieldValue = formatExtractedFieldValue;

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );
  readonly canConfirm = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canUpload = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Upload,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Cabinets.Default,
  );

  documents = signal<PagedResultDto<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);

  reviewStatusFilter = signal<DocumentReviewStatus | undefined>(undefined);
  typeFilter = signal<string>('');
  cabinetFilter = signal<string>('');
  lifecycleFilter = signal<DocumentLifecycleStatus | undefined>(undefined);
  confirmingDoc = signal<DocumentListItemDto | null>(null);
  documentTypes = signal<DocumentTypeDto[]>([]);
  cabinets = signal<CabinetDto[]>([]);
  // Dynamic ExtractedFields columns — populated only while a single documentTypeCode
  // filter is active (then the page shares one field schema). Empty for no-type /
  // mixed-type views, so the columns disappear. Driven off the type's field
  // definitions (not the union of extractedFields keys) so headers stay stable and
  // friendly even for fields no document in the page happened to fill.
  extractedFieldColumns = signal<FieldDefinitionDto[]>([]);
  selectedTypeId = signal('');
  isConfirming = signal(false);

  page = signal(0);
  pageSize = 10;
  totalPages = computed(() => Math.ceil((this.documents().totalCount ?? 0) / this.pageSize));
  paginationPages = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));
  pendingReviewCount = computed(() =>
    (this.documents().items ?? []).filter(d => d.reviewStatus === DocumentReviewStatus.PendingReview).length
  );

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;

  ngOnInit(): void {
    this.loadList();
    // Document types drive the type filter, the dynamic extracted-field columns, and
    // the confirm-classification picker. Every Documents.Default user needs them, and
    // the read is now decoupled from schema-admin permission (#223 — GetVisible no longer
    // requires DocumentTypes.Default), so load unconditionally; the error fallback keeps
    // the list usable if it ever 403s.
    this.loadDocumentTypes();
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
    this.loadExtractedFieldColumns(value);
    this.loadList();
  }

  onCabinetFilterChange(value: string): void {
    this.cabinetFilter.set(value);
    this.page.set(0);
    this.loadList();
  }

  private buildFilter(): Partial<GetDocumentListInput> {
    return {
      documentTypeCode: this.typeFilter() || undefined,
      cabinetId: this.cabinetFilter() || undefined,
      lifecycleStatus: this.lifecycleFilter(),
      reviewStatus: this.reviewStatusFilter(),
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

  // 列表只携带 documentTypeCode（出口契约）；映射到当前层可见类型的 displayName 展示，
  // 跨层 / 已删类型解析不到时回退 code。cabinets() 仍保留供顶部筛选下拉使用。
  documentTypeDisplayName(code: string | null | undefined): string | null {
    if (!code) return null;
    return this.documentTypes().find(t => t.typeCode === code)?.displayName ?? code;
  }

  // Load the selected type's field definitions and turn them into dynamic columns
  // (ordered by displayOrder). Cleared when no single type is selected. Errors fall
  // back to no columns rather than breaking the list (mirrors loadDocumentTypes).
  // The type filter is keyed by typeCode (Document exit contract); the field-definition
  // API is keyed by immutable DocumentTypeId (#207), so we resolve code → id via the
  // already-loaded visible types before querying.
  private loadExtractedFieldColumns(typeCode: string): void {
    const documentTypeId = this.documentTypes().find(t => t.typeCode === typeCode)?.id;
    if (!documentTypeId) {
      this.extractedFieldColumns.set([]);
      return;
    }
    this.fieldDefinitionService.getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: fields =>
          this.extractedFieldColumns.set(
            [...fields].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)),
          ),
        error: () => this.extractedFieldColumns.set([]),
      });
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

  delete(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status === Confirmation.Status.confirm) {
          this.documentService.delete(doc.id!)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
            next: () => {
              this.toaster.success('::Document:DeletedSuccessfully', '::Success');
              this.loadList();
            },
            error: () => this.toaster.error('::Document:DeleteFailed', '::Error'),
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
    // so the operator usually just confirms; otherwise force an explicit choice. The
    // confirm command is keyed by immutable DocumentTypeId (#207), so resolve the
    // document's exit-contract typeCode → id via the already-loaded visible types.
    this.selectedTypeId.set(
      this.documentTypes().find(t => t.typeCode === doc.documentTypeCode)?.id ?? '',
    );
  }

  closeConfirmDialog(): void {
    this.confirmingDoc.set(null);
    this.selectedTypeId.set('');
  }

  submitConfirmation(): void {
    const doc = this.confirmingDoc();
    if (!doc || !this.selectedTypeId()) return;
    this.isConfirming.set(true);
    this.documentService.confirmClassification(doc.id!, { documentTypeId: this.selectedTypeId() })
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

  getStatusBadgeClass(status: DocumentLifecycleStatus | undefined): string {
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

  getStatusLabel(status: DocumentLifecycleStatus | undefined): string {
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
}

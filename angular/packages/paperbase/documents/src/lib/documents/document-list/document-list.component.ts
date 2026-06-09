import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, formatDate } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import {
  ListService,
  LocalizationPipe,
  LocalizationService,
  PermissionService,
  escapeHtmlChars,
} from '@abp/ng.core';
import {
  EntityProp,
  EXTENSIONS_IDENTIFIER,
  ExtensionsService,
  ExtensibleTableComponent,
  ePropType,
} from '@abp/ng.components/extensible';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Confirmation } from '@abp/ng.theme.shared';
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { of } from 'rxjs';
import {
  CabinetDto,
  CabinetService,
  DocumentLifecycleStatus,
  DocumentListItemDto,
  DocumentReviewReasons,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  FieldDefinitionDto,
  FieldDefinitionService,
  GetDocumentListInput,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';
import { ClientPagedResult, configureEntityTable, PAPERBASE_TABLES } from '../../shared/extensible-table';
import { formatExtractedFieldValue } from '../../shared/format-field-value';

interface TableActivateEvent {
  type?: string;
  row?: DocumentListItemDto;
}

@Component({
  selector: 'lib-document-list',
  templateUrl: './document-list.component.html',
  styleUrls: ['./document-list.component.scss'],
  imports: [
    CommonModule,
    RouterModule,
    FormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: PAPERBASE_TABLES.Documents,
    },
  ],
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
  private readonly extensions = inject(ExtensionsService);
  private readonly locale = inject(LOCALE_ID);

  readonly list = inject(ListService);

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
  readonly hasDocumentActions = this.canConfirm || this.canDelete;

  documents = signal<ClientPagedResult<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  // Bumped whenever the dynamic columns change. ExtensibleTableComponent snapshots its
  // column list at construction, so the template keys the table on this value (@for …
  // track key) to force a fresh instance — deterministic, no setTimeout/flicker.
  tableKey = signal(0);

  hasReviewReasonsFilter = signal<boolean | undefined>(undefined);
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

  reviewNeededCount = computed(() =>
    this.documents().items.filter(d => d.requiresReview).length,
  );

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewReasons = DocumentReviewReasons;

  constructor() {
    this.rebuildTableProps([]);
  }

  ngOnInit(): void {
    this.hookListQuery();
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
    this.list.getWithoutPageReset();
  }

  onLifecycleFilterChange(value: DocumentLifecycleStatus | undefined): void {
    this.lifecycleFilter.set(value);
    this.refreshListFromFirstPage();
  }

  onTypeFilterChange(value: string): void {
    this.typeFilter.set(value);
    this.updateExtractedFieldColumns([]);
    if (value) {
      this.loadExtractedFieldColumns(value);
    }
    this.refreshListFromFirstPage();
  }

  onCabinetFilterChange(value: string): void {
    this.cabinetFilter.set(value);
    this.refreshListFromFirstPage();
  }

  private hookListQuery(): void {
    this.list.requestStatus$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status === 'idle' && this.isLoading() && this.documents().items.length === 0) return;
        this.isLoading.set(status === 'loading');
      });

    this.list
      .hookToQuery(query =>
        this.documentService.getList({
          ...this.buildFilter(),
          maxResultCount: query.maxResultCount,
          skipCount: query.skipCount,
          sorting: query.sorting || 'creationTime desc',
        }),
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(result => {
        this.documents.set({
          totalCount: result.totalCount ?? 0,
          items: result.items ?? [],
        });
      });
  }

  private refreshListFromFirstPage(): void {
    if (this.list.page === 0) {
      this.list.get();
      return;
    }

    this.list.page = 0;
  }

  private buildFilter(): Partial<GetDocumentListInput> {
    return {
      documentTypeCode: this.typeFilter() || undefined,
      cabinetId: this.cabinetFilter() || undefined,
      lifecycleStatus: this.lifecycleFilter(),
      hasReviewReasons: this.hasReviewReasonsFilter(),
    };
  }

  private rebuildTableProps(fields: FieldDefinitionDto[] = this.extractedFieldColumns()): void {
    configureEntityTable<DocumentListItemDto>(
      this.extensions,
      PAPERBASE_TABLES.Documents,
      this.createTableProps(fields),
    );
    // Force a fresh ExtensibleTableComponent so it re-reads the just-configured columns
    // (it snapshots its column list at construction). The @for key swap recreates it
    // synchronously within the same change-detection pass.
    this.tableKey.update(v => v + 1);
  }

  private createTableProps(fields: FieldDefinitionDto[]): EntityProp<DocumentListItemDto>[] {
    return [
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'fileName',
        displayName: '::Document:FileName',
        sortable: false,
        columnWidth: 340,
        valueResolver: data => {
          const doc = data.record;
          const fileName = doc.title || doc.fileOrigin?.originalFileName || '-';
          const iconClass = this.isImage(doc)
            ? 'fas fa-file-image fa-lg text-primary'
            : 'fas fa-file-pdf fa-lg text-danger';
          return of(
            `<span class="document-file-cell"><i class="${iconClass} me-2"></i><span class="fw-semibold text-truncate">${escapeHtmlChars(fileName)}</span></span>`,
          );
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'documentType',
        displayName: '::Document:Type',
        sortable: false,
        columnWidth: 180,
        valueResolver: data => {
          const typeName = this.documentTypeDisplayName(data.record.documentTypeCode);
          return of(
            typeName
              ? `<span class="badge bg-info text-dark">${escapeHtmlChars(typeName)}</span>`
              : '<span class="text-muted">-</span>',
          );
        },
      }),
      ...fields.map(field => this.createExtractedFieldProp(field)),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'status',
        displayName: '::Document:Status',
        sortable: false,
        columnWidth: 190,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          const doc = data.record;
          const spinner = this.isProcessingDocument(doc)
            ? '<span class="spinner-border spinner-border-sm me-1" role="status"></span>'
            : '';
          // #284：双 badge 可叠加——生命周期(可用性轴) + 条件 review badge(审核轴)，互不覆盖。
          const lifecycle = `<span class="${this.getStatusBadgeClass(doc.lifecycleStatus)}">${spinner}${escapeHtmlChars(localization.instant(this.getStatusLabel(doc.lifecycleStatus)))}</span>`;
          const review = doc.requiresReview
            ? ` <span class="badge bg-warning text-dark">${escapeHtmlChars(localization.instant(this.reviewBadgeLabel(doc)))}</span>`
            : '';
          return of(lifecycle + review);
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'creationTime',
        displayName: '::Document:UploadedAt',
        sortable: true,
        columnWidth: 180,
        valueResolver: data =>
          of(`<span class="text-muted small">${escapeHtmlChars(this.formatCreationTime(data.record.creationTime))}</span>`),
      }),
    ];
  }

  private createExtractedFieldProp(field: FieldDefinitionDto): EntityProp<DocumentListItemDto> {
    const fieldName = field.name ?? '';
    const propName = `extracted_${field.id || fieldName}`.replace(/[^A-Za-z0-9_]/g, '_');
    return EntityProp.create<DocumentListItemDto>({
      type: ePropType.String,
      name: propName,
      displayName: field.displayName || field.name || '',
      sortable: false,
      columnWidth: 220,
      valueResolver: data => {
        const text = formatExtractedFieldValue(data.record.extractedFields?.[fieldName]);
        const value = escapeHtmlChars(text);
        return of(`<span class="document-field-cell" title="${value}">${value}</span>`);
      },
    });
  }

  private formatCreationTime(value: string | undefined): string {
    if (!value) return '-';

    try {
      return formatDate(value, 'yyyy-MM-dd HH:mm', this.locale);
    } catch {
      return value;
    }
  }

  // Visible document types for the current layer (Host admin → Host types;
  // tenant admin → that tenant's types). Drives the confirm-classification picker.
  private loadDocumentTypes(): void {
    this.documentTypeService.getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => {
          this.documentTypes.set(types);
          if (this.typeFilter()) {
            this.loadExtractedFieldColumns(this.typeFilter());
            return;
          }
          this.rebuildTableProps([]);
        },
        error: () => {
          this.documentTypes.set([]);
          this.updateExtractedFieldColumns([]);
        },
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
      if (this.typeFilter() === typeCode) {
        this.updateExtractedFieldColumns([]);
      }
      return;
    }
    this.fieldDefinitionService.getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: fields => {
          if (this.typeFilter() !== typeCode) return;
          this.updateExtractedFieldColumns(
            [...fields].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)),
          );
        },
        error: () => {
          if (this.typeFilter() !== typeCode) return;
          this.updateExtractedFieldColumns([]);
        },
      });
  }

  private updateExtractedFieldColumns(fields: FieldDefinitionDto[]): void {
    this.extractedFieldColumns.set(fields);
    this.rebuildTableProps(fields);
  }

  onTableActivate(event: TableActivateEvent): void {
    if (event.type !== 'click' || !event.row) return;
    this.openDetail(event.row);
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
              this.list.getWithoutPageReset();
            },
            error: () => this.toaster.error('::Document:DeleteFailed', '::Error'),
          });
        }
      });
  }

  toggleManualReviewFilter(): void {
    this.hasReviewReasonsFilter.update(v => (v ? undefined : true));
    this.refreshListFromFirstPage();
  }

  // #284：只有仍"需关注"(requiresReview，服务端已含 disposition 判据——已拒绝文档不再需关注)
  // 且"分类未定"(UnresolvedClassification)才显示确认分类按钮；必填缺失走详情页补录。
  needsConfirmation(doc: DocumentListItemDto): boolean {
    return doc.requiresReview === true &&
      ((doc.reviewReasons ?? DocumentReviewReasons.None) & DocumentReviewReasons.UnresolvedClassification)
        !== DocumentReviewReasons.None;
  }

  // #284：纯可用性轴——去掉旧的 review 混判（双轴正交后两个 badge 各自渲染，不再互斥）。
  isProcessingDocument(doc: DocumentListItemDto): boolean {
    return doc.lifecycleStatus === DocumentLifecycleStatus.Processing ||
      doc.lifecycleStatus === DocumentLifecycleStatus.Uploaded;
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
        this.list.getWithoutPageReset();
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

  // #284：review badge 文案按原因——待分类确认 / 必填待补。客户端只渲染服务端给的 reviewReasons。
  reviewBadgeLabel(doc: DocumentListItemDto): string {
    const reasons = doc.reviewReasons ?? DocumentReviewReasons.None;
    if ((reasons & DocumentReviewReasons.UnresolvedClassification) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:UnresolvedClassification';
    }
    if ((reasons & DocumentReviewReasons.MissingRequiredFields) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:MissingRequiredFields';
    }
    return '::Document:NeedsReview';
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

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }
}

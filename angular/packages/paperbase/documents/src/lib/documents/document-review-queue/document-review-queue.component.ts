import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, formatDate } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ListService, LocalizationPipe, escapeHtmlChars } from '@abp/ng.core';
import {
  EntityProp,
  EXTENSIONS_IDENTIFIER,
  ExtensionsService,
  ExtensibleTableComponent,
  ePropType,
} from '@abp/ng.components/extensible';
import { ToasterService } from '@abp/ng.theme.shared';
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { of } from 'rxjs';
import {
  DocumentListItemDto,
  DocumentReviewStatus,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
} from '@dignite/paperbase';
import { ClientPagedResult, configureEntityTable, PAPERBASE_TABLES } from '../../shared/extensible-table';

interface TableActivateEvent {
  type?: string;
  row?: DocumentListItemDto;
}

@Component({
  selector: 'lib-document-review-queue',
  templateUrl: './document-review-queue.component.html',
  styleUrls: ['./document-review-queue.component.scss'],
  imports: [
    CommonModule,
    FormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: PAPERBASE_TABLES.DocumentReviewQueue,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentReviewQueueComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly router = inject(Router);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);
  private readonly locale = inject(LOCALE_ID);

  readonly list = inject(ListService);

  documents = signal<ClientPagedResult<DocumentListItemDto>>({ totalCount: 0, items: [] });
  documentTypes = signal<DocumentTypeDto[]>([]);
  isLoading = signal(true);
  isSubmitting = signal(false);

  // Confirm/assign-classification dialog state.
  classifyingDoc = signal<DocumentListItemDto | null>(null);
  selectedTypeId = signal('');

  // Reject dialog state.
  rejectingDoc = signal<DocumentListItemDto | null>(null);
  rejectReason = signal('');

  constructor() {
    configureEntityTable<DocumentListItemDto>(this.extensions, PAPERBASE_TABLES.DocumentReviewQueue, [
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
            `<span class="review-file-cell"><i class="${iconClass} me-2"></i><span class="fw-semibold text-truncate">${escapeHtmlChars(fileName)}</span></span>`,
          );
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'documentTypeCode',
        displayName: '::Document:Type',
        sortable: false,
        columnWidth: 180,
        valueResolver: data => {
          const typeCode = data.record.documentTypeCode;
          return of(typeCode
            ? `<span class="badge bg-info text-dark">${escapeHtmlChars(typeCode)}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.Number,
        name: 'classificationConfidence',
        displayName: '::Document:ClassificationConfidence',
        sortable: false,
        columnWidth: 230,
        valueResolver: data => {
          const percent = this.confidencePercent(data.record);
          const cssClass = percent < 50 ? 'text-danger' : 'text-muted';
          return of(`<span class="small ${cssClass}">${percent}%</span>`);
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'creationTime',
        displayName: '::Document:UploadedAt',
        sortable: true,
        columnWidth: 180,
        valueResolver: data =>
          of(`<span class="text-muted small">${escapeHtmlChars(this.formatDateTime(data.record.creationTime))}</span>`),
      }),
    ]);
  }

  ngOnInit(): void {
    this.hookListQuery();
    this.loadDocumentTypes();
  }

  refresh(): void {
    this.list.getWithoutPageReset();
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
          reviewStatus: DocumentReviewStatus.PendingReview,
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

  private loadDocumentTypes(): void {
    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => this.documentTypes.set(types),
        error: () => this.documentTypes.set([]),
      });
  }

  onTableActivate(event: TableActivateEvent): void {
    if (event.type !== 'click' || !event.row) return;
    this.openDetail(event.row);
  }

  openDetail(doc: DocumentListItemDto): void {
    this.router.navigate(['/documents', doc.id]);
  }

  openClassifyDialog(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.classifyingDoc.set(doc);
    // The confirm command is keyed by immutable DocumentTypeId (#207); resolve the
    // document's exit-contract typeCode → id via the already-loaded visible types.
    this.selectedTypeId.set(
      this.documentTypes().find(t => t.typeCode === doc.documentTypeCode)?.id ?? '',
    );
  }

  closeClassifyDialog(): void {
    this.classifyingDoc.set(null);
    this.selectedTypeId.set('');
  }

  submitClassify(): void {
    const doc = this.classifyingDoc();
    if (!doc || !this.selectedTypeId()) return;
    this.isSubmitting.set(true);
    this.documentService
      .confirmClassification(doc.id!, { documentTypeId: this.selectedTypeId() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.closeClassifyDialog();
          this.toaster.success('::Document:ClassificationConfirmed', '::Success');
          this.list.getWithoutPageReset();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:ConfirmFailed', '::Error');
        },
      });
  }

  openRejectDialog(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.rejectingDoc.set(doc);
    this.rejectReason.set('');
  }

  closeRejectDialog(): void {
    this.rejectingDoc.set(null);
    this.rejectReason.set('');
  }

  submitReject(): void {
    const doc = this.rejectingDoc();
    if (!doc) return;
    this.isSubmitting.set(true);
    const reason = this.rejectReason().trim();
    this.documentService
      .rejectReview(doc.id!, { reason: reason || undefined })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.closeRejectDialog();
          this.toaster.success('::Document:Review:RejectedSuccessfully', '::Success');
          this.list.getWithoutPageReset();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:Review:ActionFailed', '::Error');
        },
      });
  }

  confidencePercent(doc: DocumentListItemDto): number {
    return Math.round((doc.classificationConfidence ?? 0) * 100);
  }

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }

  private formatDateTime(value: string | undefined): string {
    if (!value) return '-';

    try {
      return formatDate(value, 'yyyy-MM-dd HH:mm', this.locale);
    } catch {
      return value;
    }
  }
}

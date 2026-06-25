import { ChangeDetectionStrategy, Component, DestroyRef, LOCALE_ID, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, formatDate } from '@angular/common';
import { ListService, LocalizationPipe, PermissionService, escapeHtmlChars } from '@abp/ng.core';
import {
  EntityProp,
  EXTENSIONS_IDENTIFIER,
  ExtensionsService,
  ExtensibleTableComponent,
  ePropType,
} from '@abp/ng.components/extensible';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { of } from 'rxjs';
import {
  DocumentListItemDto,
  DocumentService,
  EXTRACT_PERMISSIONS,
} from '@dignite/vault-extract';
import { ClientPagedResult, configureEntityTable, EXTRACT_TABLES } from '../../shared/extensible-table';
import { formatBytes } from '../../shared/format-bytes';

@Component({
  selector: 'lib-document-recycle-bin',
  templateUrl: './document-recycle-bin.component.html',
  styleUrls: ['./document-recycle-bin.component.scss'],
  imports: [CommonModule, LocalizationPipe, ExtensibleTableComponent, NgbDropdownModule],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: EXTRACT_TABLES.DocumentRecycleBin,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentRecycleBinComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);
  private readonly locale = inject(LOCALE_ID);

  readonly list = inject(ListService);

  documents = signal<ClientPagedResult<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);

  readonly canRestore = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Restore,
  );
  readonly canPermanentDelete = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.PermanentDelete,
  );
  readonly hasRecycleActions = this.canRestore || this.canPermanentDelete;

  constructor() {
    configureEntityTable<DocumentListItemDto>(this.extensions, EXTRACT_TABLES.DocumentRecycleBin, [
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
            ? 'fas fa-file-image fa-lg text-muted'
            : 'fas fa-file-pdf fa-lg text-muted';
          return of(
            `<span class="recycle-file-cell"><i class="${iconClass} me-2"></i><span class="text-muted text-truncate">${escapeHtmlChars(fileName)}</span></span>`,
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
            ? `<span class="badge bg-secondary">${escapeHtmlChars(typeCode)}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'fileSize',
        displayName: '::Document:Size',
        sortable: false,
        columnWidth: 140,
        valueResolver: data =>
          of(`<span class="text-muted small">${escapeHtmlChars(formatBytes(data.record.fileOrigin?.fileSize))}</span>`),
      }),
      EntityProp.create<DocumentListItemDto>({
        type: ePropType.String,
        name: 'deletionTime',
        displayName: '::Document:DeletedAt',
        sortable: false,
        columnWidth: 180,
        valueResolver: data =>
          of(`<span class="text-muted small">${escapeHtmlChars(this.formatDateTime(data.record.deletionTime))}</span>`),
      }),
    ]);
  }

  ngOnInit(): void {
    this.hookListQuery();
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
          isDeleted: true,
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

  restore(doc: DocumentListItemDto): void {
    this.confirmation
      .warn('::Document:AreYouSureToRestore', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.restore(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Document:RestoredSuccessfully', '::Success');
              this.list.getWithoutPageReset();
            },
            error: () => this.toaster.error('::Document:RestoreFailed', '::Error'),
          });
      });
  }

  permanentDelete(doc: DocumentListItemDto): void {
    this.confirmation
      .warn('::Document:AreYouSureToPermanentlyDelete', '::AreYouSure', {
        yesText: '::Document:PermanentDelete',
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.permanentDelete(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Document:PermanentlyDeletedSuccessfully', '::Success');
              this.list.getWithoutPageReset();
            },
            error: () => this.toaster.error('::Document:PermanentDeleteFailed', '::Error'),
          });
      });
  }

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }

  private formatDateTime(value: string | null | undefined): string {
    if (!value) return '-';

    try {
      return formatDate(value, 'yyyy-MM-dd HH:mm', this.locale);
    } catch {
      return value;
    }
  }
}

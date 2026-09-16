import { ChangeDetectionStrategy, Component, DestroyRef, LOCALE_ID, OnInit, computed, inject, signal } from '@angular/core';
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
import { finalize, of } from 'rxjs';
import {
  DocumentListItemDto,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { ClientPagedResult, configureEntityTable, EXTRACT_TABLES } from '../../shared/extensible-table';
import { executeBulkOperations } from '../../shared/bulk-operation';
import { formatBytes } from '../../shared/format-bytes';
import {
  canRestoreAnyDocumentType,
  documentRightsAccessor,
  readDocumentModuleWidePolicies,
} from '../../shared/document-rights';

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
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);
  private readonly locale = inject(LOCALE_ID);

  readonly list = inject(ListService);

  documents = signal<ClientPagedResult<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  selectedDocuments = signal<DocumentListItemDto[]>([]);
  isBulkDeleting = signal(false);
  documentTypes = signal<DocumentTypeDto[]>([]);
  readonly selectedCount = computed(() => this.selectedDocuments().length);

  // #632 code review: the types fetch FAILING is a third state, distinct from both "no rows" and "you may
  // restore nothing". Without it a transient network error read as an empty grant dictionary, so
  // canRestoreAnything() answered false and the page told a caller who does hold a Delete grant to go ask an
  // administrator for one — a wrong and unactionable instruction, and the list query was never hooked, which
  // left the Refresh button inert so nothing could recover it. DocumentUploadComponent already separates these
  // two states for the same fetch (#629); this is the same separation on this page.
  readonly typesUnavailable = signal(false);

  // Whether hookToQuery has run. hookToQuery must happen exactly once per component — a second call would
  // add a second subscription and issue every request twice — and until it has, ListService.getWithoutPageReset
  // has no query to re-run, which is what made refresh() a no-op in the two states that skip the hook.
  private listHooked = false;

  // #632: the module-wide half of the rules, snapshotted once. "May restore" is now module-wide
  // Documents.Restore OR a Delete grant on the row's own document type — whoever may delete may undo — so it
  // is no longer one boolean field, and this page needs the visible types to answer it at all.
  private readonly moduleWideRights = readDocumentModuleWidePolicies(this.permissionService);

  // #632: per-row restore right. Reads the documentTypes signal on every call, so rows re-evaluate as soon as
  // the type list (and its grant dictionary) lands.
  readonly rightsFor = documentRightsAccessor(this.documentTypes, this.moduleWideRights);

  // Permanent delete stays module-wide, by decision: no per-type grant reaches it.
  readonly canPermanentDelete = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.PermanentDelete,
  );

  // #632: "may this caller restore anything at all" — the client twin of the server's CheckOnAnyTypeAsync
  // gate on the recycle-bin list. The route is now the entry permission (a per-type grant cannot be expressed
  // as a route policy), so this is what decides whether the page queries the server at all; asking without it
  // would earn a 403 toast the caller can do nothing about.
  readonly canRestoreAnything = computed(() =>
    canRestoreAnyDocumentType(this.documentTypes(), this.moduleWideRights),
  );

  // The actions column exists when some row on the page actually carries an action.
  readonly hasRecycleActions = computed(
    () => this.canPermanentDelete || this.documents().items.some(d => this.rightsFor(d).canRestore),
  );

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
    this.loadDocumentTypes();
  }

  // The visible types carry this caller's own per-type grant dictionary, which is half of "may restore". The
  // list query is hooked only afterwards, and only when the answer is yes — the server refuses the recycle-bin
  // list to a caller who may restore nothing, and the route now admits every documents user.
  private loadDocumentTypes(): void {
    this.isLoading.set(true);
    this.typesUnavailable.set(false);

    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => {
          this.documentTypes.set(types);
          this.startListing();
        },
        error: () => {
          // NOT "you may restore nothing": this call failed, so the caller's grants are unknown, and the page
          // says so and offers the retry rather than reporting the absence of an answer as a denial.
          this.documentTypes.set([]);
          this.typesUnavailable.set(true);
          this.isLoading.set(false);
        },
      });
  }

  private startListing(): void {
    if (!this.canRestoreAnything()) {
      // Nothing to show and nothing to ask for: the page falls through to its own empty state.
      this.isLoading.set(false);
      return;
    }
    this.hookListQuery();
  }

  // The header's Refresh button, and the retry offered by the unavailable state. In both states that skip the
  // hook — the fetch failed, or the caller may restore nothing — there is no query to re-run, so it re-runs the
  // types fetch instead: that is what recovers a transient failure, and it also re-reads a grant an
  // administrator may have added since the page loaded. Once the list is hooked it is the list that refreshes.
  refresh(): void {
    if (this.listHooked) {
      this.list.getWithoutPageReset();
      return;
    }
    this.loadDocumentTypes();
  }

  private hookListQuery(): void {
    this.listHooked = true;

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
        const items = result.items ?? [];
        this.documents.set({
          totalCount: result.totalCount ?? 0,
          items,
        });
        this.reconcileSelection(items);
      });
  }

  onSelectionChange(selected: DocumentListItemDto[]): void {
    this.selectedDocuments.set([...selected]);
  }

  clearSelection(): void {
    this.selectedDocuments.set([]);
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
              this.clearSelection();
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
              this.clearSelection();
              this.list.getWithoutPageReset();
            },
            error: () => this.toaster.error('::Document:PermanentDeleteFailed', '::Error'),
          });
      });
  }

  bulkPermanentDelete(): void {
    // Permanent deletion enforces the same parent/child integrity rule, including recycle-bin
    // children. Delete selected non-containers before their containers and keep the order serial.
    const selected = [...this.selectedDocuments()]
      .sort((left, right) => Number(!!left.isContainer) - Number(!!right.isContainer));
    if (selected.length === 0 || this.isBulkDeleting()) return;

    this.confirmation
      .warn('::Document:Bulk:PermanentDeleteConfirm', '::AreYouSure', {
        yesText: '::Document:Bulk:PermanentDelete',
        messageLocalizationParams: [String(selected.length)],
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;

        this.isBulkDeleting.set(true);
        executeBulkOperations(selected, doc => this.documentService.permanentDelete(doc.id!), 1)
          .pipe(
            finalize(() => this.isBulkDeleting.set(false)),
            takeUntilDestroyed(this.destroyRef),
          )
          .subscribe(result => {
            const succeeded = result.succeeded.length;
            const failed = result.failed.length;
            if (failed === 0) {
              this.toaster.success('::Document:Bulk:PermanentDeleteSucceeded', '::Success', {
                messageLocalizationParams: [String(succeeded)],
              });
            } else if (succeeded > 0) {
              this.toaster.warn('::Document:Bulk:PermanentDeletePartial', 'AbpUi::Warning', {
                messageLocalizationParams: [String(succeeded), String(failed)],
              });
            } else {
              this.toaster.error('::Document:Bulk:PermanentDeleteFailed', '::Error', {
                messageLocalizationParams: [String(failed)],
              });
            }

            this.clearSelection();
            this.list.getWithoutPageReset();
          });
      });
  }

  private reconcileSelection(items: DocumentListItemDto[]): void {
    const selectedIds = new Set(this.selectedDocuments().map(doc => doc.id).filter(Boolean));
    this.selectedDocuments.set(items.filter(doc => !!doc.id && selectedIds.has(doc.id)));
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

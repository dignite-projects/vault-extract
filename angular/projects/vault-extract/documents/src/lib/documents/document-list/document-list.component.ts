import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, DOCUMENT, formatDate } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
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
import { finalize, of, Subscription, timer } from 'rxjs';
import {
  CabinetDto,
  CabinetService,
  DocumentFieldFilter,
  DocumentLifecycleStatus,
  DocumentListItemDto,
  DocumentListQueryService,
  DocumentReviewReasons,
  DocumentService,
  DocumentStatisticsService,
  ExportFormat,
  DocumentExportService,
  FieldDefinitionDto,
  FieldDefinitionService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { ClientPagedResult, configureEntityTable, EXTRACT_TABLES } from '../../shared/extensible-table';
import { formatExtractedFieldValue } from '../../shared/format-field-value';
import { FieldValueFilterComponent } from '../../shared/field-value-filter/field-value-filter.component';
import { isFilterableField } from '../../shared/field-value-filter/field-value-filter.model';
import { exportFileName, readBlobErrorMessage, triggerBlobDownload } from '../../shared/blob-download';
import { executeBulkOperations } from '../../shared/bulk-operation';
import {
  assignableDocumentTypes,
  canEditAnyDocumentType,
  rightsOf,
} from '../../shared/document-access';
import { DocumentTypesStore } from '../../shared/document-types.store';
import { DocumentListFilter, toExportDocumentsInput } from './export-current-view';

interface TableActivateEvent {
  type?: string;
  row?: DocumentListItemDto;
  event?: Event;
}

// #440 (interim live status, list surface): while any document on the current page is still processing,
// silently re-fetch the list so the operator can watch upload/processing progress without a manual Refresh.
// Mirrors the detail page's #440 poll — silent, tab-visibility-aware, self-terminating — but on a single
// relaxed interval rather than the detail page's fast-start backoff: the list is a heavier, less
// latency-sensitive query and the operator wants a calm, predictable cadence, not full-rate polling. To be
// superseded by server push (SSE / SignalR), see #440.
const LIST_POLL_INTERVAL_MS = 10000;

@Component({
  selector: 'lib-document-list',
  templateUrl: './document-list.component.html',
  styleUrls: ['./document-list.component.scss'],
  imports: [
    CommonModule,
    FormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
    FieldValueFilterComponent,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: EXTRACT_TABLES.Documents,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentListComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  // #415 fix: the list GET goes through the hand-written wrapper, which serializes the fieldFilters
  // collection into the indexed query notation ASP.NET Core binds (the generated getList sends
  // "[object Object]"). All other document operations still use the generated DocumentService.
  private readonly documentListQueryService = inject(DocumentListQueryService);
  private readonly statisticsService = inject(DocumentStatisticsService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly cabinetService = inject(CabinetService);
  private readonly documentExportService = inject(DocumentExportService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  // #440: DOM document handle for the Page Visibility check that pauses the background poll on a hidden tab.
  private readonly domDocument = inject(DOCUMENT);
  private readonly extensions = inject(ExtensionsService);
  private readonly locale = inject(LOCALE_ID);
  // #635 decision 7: the visible types come from the one shared store — the type filter, the dynamic
  // extracted-field columns, the confirm picker and the review-queue gate all read the same fetch.
  readonly documentTypes = inject(DocumentTypesStore);

  readonly list = inject(ListService);

  // #635: the module-wide half of the two questions the client still answers for itself — which types may
  // this caller declare, and may it review anything at all. Snapshotted once, per the repo convention:
  // getGrantedPolicy re-parses its policy expression on every call and these run inside templates.
  readonly canConfirmClassification = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canUpload = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Upload,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Default,
  );
  // #496 / #499: "download current view" is now the only download surface, gated on Documents.Export.
  // The rows inside the file are narrowed server-side by the caller's read scope (#632 decision 3), so the
  // button itself stays module-wide.
  readonly canExport = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Documents.Export);
  // #632: whole-layer statistics (DocumentStatisticsAppService) now require Documents.ReadAll, so the
  // needs-review badge count must not be fetched without it — the call would 403.
  readonly canReadAll = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Documents.ReadAll);

  documents = signal<ClientPagedResult<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  // Bumped whenever the dynamic columns change. ExtensibleTableComponent snapshots its
  // column list at construction, so the template keys the table on this value (@for …
  // track key) to force a fresh instance — deterministic, no setTimeout/flicker.
  tableKey = signal(0);

  typeFilter = signal<string>('');
  cabinetFilter = signal<string>('');
  lifecycleFilter = signal<DocumentLifecycleStatus | undefined>(undefined);
  // #395: needs-review filter. Replaces the standalone review-queue page — when on, the list shows only
  // documents that still require operator attention (hasReviewReasons, the queue's RequiresAttention
  // predicate). Seeded from the ?review=1 deep-link used by the overview needs-review entry points.
  reviewFilter = signal<boolean>(false);
  // #354: when set, the list shows only the sub-documents derived from this source document (a container's
  // children). subDocumentsParent is the container itself (for the indicator banner); it is null when the filter
  // was seeded from a deep-link query param and the parent row is not in hand.
  originDocumentIdFilter = signal<string>('');
  subDocumentsParent = signal<DocumentListItemDto | null>(null);
  confirmingDoc = signal<DocumentListItemDto | null>(null);
  cabinets = signal<CabinetDto[]>([]);
  // Dynamic ExtractedFields columns — populated only while a single documentTypeCode
  // filter is active (then the page shares one field schema). Empty for no-type /
  // mixed-type views, so the columns disappear. Driven off the type's field
  // definitions (not the union of extractedFields keys) so headers stay stable and
  // friendly even for fields no document in the page happened to fill.
  extractedFieldColumns = signal<FieldDefinitionDto[]>([]);
  // Which field types index anything at all, keyed by registration name — straight from the server via
  // FieldDefinitionAppService.GetFieldTypesAsync (IFieldType.IndexValueType). Empty until that request
  // lands. Mirrors FieldValueFilterComponent's own map (and its isFilterableField predicate below):
  // a field is a list column only if its type is structurally indexable AND its own IsSearchable is on —
  // this is what keeps CKEditor/Table (whose IndexValueType is null) out of the dynamic columns without
  // a per-type isMarkdown/isTable branch, and without a separate "show in list" flag (#628 follow-up).
  private indexableByFieldType = new Map<string, boolean>();
  // The current type's field definitions before the isFilterableField filter, kept so the filter can be
  // re-applied once indexableByFieldType lands (it is fetched independently and may resolve after the
  // fields themselves, per field-definition-list.component.ts's own comment on the same race).
  private rawExtractedFields: FieldDefinitionDto[] = [];
  // #415: extracted-field-value filters composed by lib-field-value-filter. AND-combined with the metadata
  // filters and sent as GetDocumentListInput.fieldFilters. Only ever non-empty while a single document type
  // is selected (the backend requires documentTypeCode when fieldFilters are present); cleared on type
  // change so one type's field filters can never be applied to another type or to a no-type view.
  fieldValueFilters = signal<DocumentFieldFilter[]>([]);
  selectedTypeId = signal('');
  isConfirming = signal(false);
  isExporting = signal(false);
  selectedDocuments = signal<DocumentListItemDto[]>([]);
  isBulkDeleting = signal(false);
  readonly selectedCount = computed(() => this.selectedDocuments().length);

  // #284 review-queue gateway: the toolbar badge shows the canonical needs-review total for the current
  // layer (DocumentStatisticsDto.NeedsReviewCount — same RequiresAttention predicate the review queue runs,
  // #333), not a page-local count, so it stays correct across pagination and once the list is unfiltered.
  reviewQueueCount = signal(0);

  // #635 decision 5: every per-row action reads the verdict the server sent down with the row. The client no
  // longer re-derives it from the caller's grants and the row's type — that copy could not see ownership at
  // all, and keyed types by code against the ACTIVE types, so a document on an archived type lost every
  // action the API would in fact have admitted.
  readonly rightsOf = rightsOf;

  // #632: only the types this caller may ASSIGN may appear in the confirm picker — ConfirmClassification
  // module-wide, or an Upload grant on that particular type (the #629 rule, unchanged). Offering any other
  // type would build a request the server refuses. Still a client-side answer after #635 because it is a
  // question about TYPES: the target type of a reclassification is not the row being judged.
  readonly assignableTypes = computed(() =>
    assignableDocumentTypes(this.documentTypes.value(), this.canConfirmClassification),
  );

  // #632: the needs-review toggle is a FILTER, not a per-document action, so there is no single document to
  // judge it against — it is offered when the caller may run the edit family on anything at all (module-wide,
  // or through an Edit grant on at least one visible type). The badge beside it is a whole-layer statistic and
  // is gated separately on ReadAll (loadReviewQueueCount).
  readonly canReviewAnyType = computed(() =>
    canEditAnyDocumentType(this.documentTypes.value(), this.canConfirmClassification),
  );

  // #632: any row on the current page the caller may delete. Drives the selection checkboxes, which
  // abp-extensible-table only exposes as a single table-wide flag — the per-row narrowing happens in
  // onSelectionChange instead.
  readonly canDeleteAnyRow = computed(() =>
    this.documents().items.some(d => rightsOf(d).canDelete),
  );

  // #354: render the row actions column when the user has confirm/delete actions on some row OR any row is a
  // container (exposes "view sub-documents") OR any row is a sub-document (exposes "view parent / view
  // siblings") — these provenance actions are available regardless of confirm/delete permissions.
  // #632: "has confirm/delete actions" is now decided per row rather than by a module-wide permission pair.
  readonly showActionsColumn = computed(() =>
    this.documents().items.some(d => {
      const rights = rightsOf(d);
      return rights.canEdit || rights.canDelete || d.isContainer || d.originDocumentId;
    }),
  );

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewReasons = DocumentReviewReasons;
  readonly ExportFormat = ExportFormat;

  // #440: subscription to the pending background poll tick (null when not polling).
  private pollTimer: Subscription | null = null;
  // #440: true while a silent poll request is in flight, so the requestStatus$ subscription leaves the
  // full-page spinner and the Refresh-button spin untouched and the table refreshes in place.
  private isPolling = false;

  constructor() {
    this.rebuildTableProps([]);
    this.fieldDefinitionService.getFieldTypes()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(fieldTypes => {
        this.indexableByFieldType = new Map(
          fieldTypes.map(fieldType => [fieldType.name ?? '', fieldType.indexable ?? true]),
        );
        // Columns already built (or left empty) against a not-yet-loaded map need to catch up now.
        this.applyExtractedFieldColumns(this.rawExtractedFields);
      });
    // #440: pause the background poll while the tab is hidden (no point re-fetching an unseen list) and resume
    // with an immediate refresh when it returns to the foreground; always tear the timer down on destroy so
    // navigating away stops polling.
    const onVisibilityChange = () => {
      if (this.domDocument.hidden) {
        this.clearPollTimer();
      } else if (this.shouldPoll() && !this.pollTimer) {
        this.pollReload();
      }
    };
    this.domDocument.addEventListener('visibilitychange', onVisibilityChange);
    this.destroyRef.onDestroy(() => {
      this.domDocument.removeEventListener('visibilitychange', onVisibilityChange);
      this.clearPollTimer();
    });
    // #635 decision 7: the two loads that depend on the visible types used to be continuations of this
    // page's own getVisible() callbacks. The types now come from a shared store that may already hold them,
    // so both follow the store's state instead: run once it has settled, ready or failed. The error case has
    // to reach them too — an empty type list is the module-wide-only answer, which is a real answer.
    effect(() => {
      if (this.documentTypes.isLoading()) {
        return;
      }
      // Read so the effect re-runs when a reload changes the answer; the body is untracked so its own signal
      // reads (the type filter, the request states it writes) cannot make it a dependency of itself.
      this.documentTypes.value();
      untracked(() => {
        this.loadReviewQueueCount();
        this.rebuildExtractedFieldColumns();
      });
    });
  }

  ngOnInit(): void {
    // Seed filters from query params first so the overview cards (#335) can deep-link into a
    // cabinet- or type-filtered list before the initial load runs.
    this.applyQueryParamFilters();
    // Seed page + sorting into the ListService BEFORE hookToQuery so the initial fetch uses them (the
    // query$ pipe debounces, so these synchronous seeds coalesce with the constructor's default into a
    // single request). This is what makes Back restore the exact page/sort the operator left, not just the
    // filters.
    this.applyQueryParamPaging();
    this.hookListQuery();
    // #635: the type store fetches once per session; a failed first fetch would otherwise leave this page
    // without its type filter and dynamic columns until a full reload. A no-op unless it failed.
    this.documentTypes.retryIfFailed();
    // Cabinet getList is gated by Cabinets.Default; only fetch when granted to
    // avoid a 403 for users without cabinet access (cabinet filter/labels hidden).
    if (this.canViewCabinets) {
      this.loadCabinets();
    }
  }

  refresh(): void {
    this.documentTypes.retryIfFailed();
    this.list.getWithoutPageReset();
  }

  // Overview deep-links (#335) carry cabinetId / documentTypeCode in the URL, and every filter change now
  // writes the active filter set back via writeFiltersToUrl(). Seed the matching filter signals once on
  // load; the top dropdowns are bound to the same signals so they reflect the applied value, and
  // loadDocumentTypes() picks up a seeded typeFilter to load its field columns. Because the filters live in
  // the URL, opening a document and navigating Back restores the exact filtered view instead of resetting
  // to the unfiltered default.
  private applyQueryParamFilters(): void {
    const params = this.route.snapshot.queryParamMap;
    const cabinetId = params.get('cabinetId');
    if (cabinetId) {
      this.cabinetFilter.set(cabinetId);
    }
    const typeCode = params.get('documentTypeCode');
    if (typeCode) {
      this.typeFilter.set(typeCode);
    }
    const lifecycleStatus = params.get('lifecycleStatus');
    if (lifecycleStatus) {
      const parsed = Number(lifecycleStatus);
      if (!Number.isNaN(parsed)) {
        this.lifecycleFilter.set(parsed as DocumentLifecycleStatus);
      }
    }
    // #354: deep-link into a container's sub-documents (the parent row may not be loaded, so the banner falls
    // back to showing the id until/unless the operator navigated via the in-list "view sub-documents" action).
    const originDocumentId = params.get('originDocumentId');
    if (originDocumentId) {
      this.originDocumentIdFilter.set(originDocumentId);
    }
    // #395: overview needs-review entry points deep-link here with ?review=1.
    if (params.get('review')) {
      this.reviewFilter.set(true);
    }
  }

  // Seed the ListService's page + sorting from the URL. ABP's ListService has no built-in URL binding, but
  // page / sortKey / sortOrder are public read/write, so writing them here makes the first fetch (and the
  // restored view after Back) start on the right page and ordering. The header sort arrow is owned by the
  // table and not re-seeded, so only the data ordering is restored — acceptable, and the only sortable
  // column is the default creationTime.
  private applyQueryParamPaging(): void {
    const params = this.route.snapshot.queryParamMap;
    const page = Number(params.get('page'));
    if (Number.isInteger(page) && page > 0) {
      this.list.page = page;
    }
    const sorting = params.get('sorting');
    if (sorting) {
      const [key, order] = sorting.split(' ');
      if (key && (order === 'asc' || order === 'desc')) {
        this.list.sortKey = key;
        this.list.sortOrder = order;
      }
    }
  }

  onLifecycleFilterChange(value: DocumentLifecycleStatus | undefined): void {
    this.lifecycleFilter.set(value);
    this.refreshListFromFirstPage();
  }

  onTypeFilterChange(value: string): void {
    this.typeFilter.set(value);
    // #415: the previous type's field filters cannot apply to another type (or to a no-type view). Drop
    // them so they are neither sent on the refresh below nor left dangling; the composer resets its own
    // rows when its fieldDefinitions input changes.
    this.fieldValueFilters.set([]);
    this.applyExtractedFieldColumns([]);
    if (value) {
      this.loadExtractedFieldColumns(value);
    }
    this.refreshListFromFirstPage();
  }

  // #415: the field-value composer emits its composed, server-shaped filters on Apply (and [] on Clear).
  // Store them and re-query from page 1 so pagination/count stay consistent; buildFilter folds them into
  // GetDocumentListInput.fieldFilters, AND-combined with the metadata filters.
  onFieldFiltersChange(filters: DocumentFieldFilter[]): void {
    this.fieldValueFilters.set(filters);
    this.refreshListFromFirstPage();
  }

  onCabinetFilterChange(value: string): void {
    this.cabinetFilter.set(value);
    this.refreshListFromFirstPage();
  }

  // #395: toggle the needs-review filter (the former review-queue gateway). Refreshes from page 1 so the
  // filtered count and pagination stay consistent.
  toggleReviewFilter(): void {
    this.reviewFilter.update(on => !on);
    this.refreshListFromFirstPage();
  }

  // #354: focus the list on a container's sub-documents (those whose OriginDocumentId is this container).
  viewSubDocuments(doc: DocumentListItemDto, event?: Event): void {
    event?.stopPropagation();
    if (!doc.id) {
      return;
    }
    this.subDocumentsParent.set(doc);
    this.originDocumentIdFilter.set(doc.id);
    this.refreshListFromFirstPage();
  }

  clearSubDocumentsFilter(): void {
    this.subDocumentsParent.set(null);
    this.originDocumentIdFilter.set('');
    this.refreshListFromFirstPage();
  }

  // #354: from a sub-document row, open its source (container) document.
  openParentDocument(doc: DocumentListItemDto, event?: Event): void {
    event?.stopPropagation();
    if (!doc.originDocumentId) {
      return;
    }
    this.router.navigate(['/documents', doc.originDocumentId]);
  }

  // #354: from a sub-document row, focus the list on its siblings (all sub-documents of the same source,
  // including this one). The parent row may not be in hand, so the banner falls back to showing the id.
  viewSiblingDocuments(doc: DocumentListItemDto, event?: Event): void {
    event?.stopPropagation();
    if (!doc.originDocumentId) {
      return;
    }
    this.subDocumentsParent.set(null);
    this.originDocumentIdFilter.set(doc.originDocumentId);
    this.refreshListFromFirstPage();
  }

  private hookListQuery(): void {
    this.list.requestStatus$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        // #440: a failed request (a poll tick included) ends the quiet window and stops the poll rather than
        // hammering a failing endpoint; the next successful load restarts it. Clearing isPolling here also lets
        // a later manual Refresh show its spinner again.
        if (status === 'error') {
          this.isPolling = false;
          this.clearPollTimer();
        }
        // #440: a background poll tick must not flip the full-page spinner or the Refresh-button spin — the
        // rows refresh in place. While a silent poll is in flight, leave isLoading untouched.
        if (this.isPolling) return;
        if (status === 'idle' && this.isLoading() && this.documents().items.length === 0) return;
        this.isLoading.set(status === 'loading');
      });

    // query$ is the single funnel for every page / sort / filter change (the table writes page+sort here
    // directly; our filter handlers reach it via refreshListFromFirstPage). Persist the full view state to
    // the URL on each emit so table-driven pagination and sorting survive a round-trip to a document and
    // Back, alongside the filters. (It is debounced inside ListService, so this does not fire per keystroke.)
    this.list.query$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.writeStateToUrl());

    this.list
      .hookToQuery(query =>
        // #415 fix: via the wrapper so fieldFilters serializes to bindable indexed query params.
        this.documentListQueryService.getList({
          ...this.buildFilter(),
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
        // #440: a load has landed (initial, filter change, manual Refresh, or a poll tick alike). Drop the
        // quiet flag and (re)schedule or stop the poll from the freshly loaded page — so polling starts when a
        // page still holds processing documents and stops once none remain.
        this.isPolling = false;
        this.syncPolling();
      });
  }

  // #440: a background poll tick. Silently re-fetches the current page (getWithoutPageReset keeps the operator
  // on their page/sort) — never toggles isLoading, guarded via isPolling in the requestStatus$ subscription.
  private pollReload(): void {
    this.isPolling = true;
    this.list.getWithoutPageReset();
  }

  // #440: poll only while at least one row on the current page is still genuinely advancing (Uploaded or
  // Processing). Post-#510 a document parked on a blocking review reason is PendingReview (not Processing), so
  // this can never get stuck polling forever waiting on the operator — Ready / Failed / PendingReview all end
  // the loop once no row is still processing.
  private shouldPoll(): boolean {
    return this.documents().items.some(doc => this.isProcessingDocument(doc));
  }

  // #440: called after every load. Cancels any pending tick, then — if the page still holds a processing
  // document and the tab is visible — schedules the next silent re-fetch.
  private syncPolling(): void {
    this.clearPollTimer();
    if (!this.shouldPoll() || this.domDocument.hidden) {
      return; // hidden tabs resume via the visibilitychange handler in the constructor
    }
    this.pollTimer = timer(LIST_POLL_INTERVAL_MS).subscribe(() => this.pollReload());
  }

  private clearPollTimer(): void {
    this.pollTimer?.unsubscribe();
    this.pollTimer = null;
  }

  private refreshListFromFirstPage(): void {
    this.clearSelection();
    // Reset to page 1 first (the setter triggers the refetch), then persist — so the URL records page 0,
    // not the page the operator was on before changing the filter. The debounced query$ subscription will
    // also fire and re-persist the same state; writing here too keeps the URL correct synchronously, so a
    // filter-then-immediately-open-a-document sequence still records the new filters before navigating away.
    if (this.list.page === 0) {
      this.list.get();
    } else {
      this.list.page = 0;
    }
    this.writeStateToUrl();
  }

  // Mirror the active filters AND the ListService's page/sorting into the URL query string. replaceUrl keeps
  // rapid changes from piling up Back-button steps (each replaces the current /documents/list entry rather
  // than pushing a new one); null values are dropped by the merge handling, so cleared filters and the
  // default page/sort leave a clean URL. applyQueryParamFilters + applyQueryParamPaging re-seed from here on
  // Back, restoring the exact view.
  private writeStateToUrl(): void {
    const sorting = this.list.sortOrder
      ? `${this.list.sortKey} ${this.list.sortOrder}`
      : null;
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        lifecycleStatus: this.lifecycleFilter() ?? null,
        documentTypeCode: this.typeFilter() || null,
        cabinetId: this.cabinetFilter() || null,
        originDocumentId: this.originDocumentIdFilter() || null,
        review: this.reviewFilter() ? 1 : null,
        page: this.list.page > 0 ? this.list.page : null,
        sorting,
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  // #496: typed as DocumentListFilter (not Partial<GetDocumentListInput>) so the compile-time round-trip
  // guard in export-current-view.ts can assert every key here survives into the export contract.
  private buildFilter(): DocumentListFilter {
    const fieldFilters = this.fieldValueFilters();
    return {
      documentTypeCode: this.typeFilter() || undefined,
      cabinetId: this.cabinetFilter() || undefined,
      originDocumentId: this.originDocumentIdFilter() || undefined,
      lifecycleStatus: this.lifecycleFilter(),
      // #395: same RequiresAttention predicate the old review queue ran.
      hasReviewReasons: this.reviewFilter() || undefined,
      // #415: type-scoped extracted-field-value filters, AND-combined server-side with the above. Omitted
      // when empty so an unfiltered query keeps its previous shape; only ever populated with a type selected.
      fieldFilters: fieldFilters.length ? fieldFilters : undefined,
    };
  }

  private rebuildTableProps(fields: FieldDefinitionDto[] = this.extractedFieldColumns()): void {
    configureEntityTable<DocumentListItemDto>(
      this.extensions,
      EXTRACT_TABLES.Documents,
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
          const localization = data.getInjected(LocalizationService);
          const fileName = doc.title || doc.fileOrigin?.originalFileName || '-';
          const iconClass = this.isImage(doc)
            ? 'fas fa-file-image fa-lg text-primary'
            : 'fas fa-file-pdf fa-lg text-danger';
          // #350: a container is a bundle of sub-documents and is not itself a business record. Flag it
          // with a badge so operators don't mistake it for a normal document. isContainer is a
          // system-controlled signal carried on the list DTO. #354: the row's "view sub-documents" action
          // (containers only) drills into its children via the originDocumentId filter.
          const bundleBadge = doc.isContainer
            ? ` <span class="badge bg-dark">${escapeHtmlChars(localization.instant('::Document:Bundle'))}</span>`
            : '';
          // #354: mirror of the container Bundle badge on the child side — a sub-document carries
          // originDocumentId (its source) and is flagged so operators can tell it apart from a
          // normally-uploaded document; the row's "view parent / view siblings" actions drill the relationship.
          const subDocBadge = doc.originDocumentId
            ? ` <span class="badge bg-secondary">${escapeHtmlChars(localization.instant('::Document:SubDocument'))}</span>`
            : '';
          return of(
            `<span class="document-file-cell"><i class="${iconClass} me-2"></i><span class="fw-semibold text-truncate">${escapeHtmlChars(fileName)}</span>${bundleBadge}${subDocBadge}</span>`,
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
          // #284: two badges may stack: lifecycle on the availability axis plus conditional review badge
          // on the review axis. They do not overwrite each other.
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

  // The dynamic extracted-field columns need a type ID, and the filter carries a type CODE, so they can only
  // be built once the visible types are in hand. Called from the store-following effect in the constructor.
  private rebuildExtractedFieldColumns(): void {
    const typeCode = this.typeFilter();
    if (typeCode && this.documentTypes.value().length > 0) {
      this.loadExtractedFieldColumns(typeCode);
      return;
    }
    // No single type selected, or the types could not be loaded at all: no dynamic columns, and the list
    // itself stays usable.
    this.applyExtractedFieldColumns([]);
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

  /**
   * #496 / #499: download exactly the rows on screen, carrying every field the selected type defines. The
   * export input is projected from the same buildFilter() the list query runs, so the file cannot disagree
   * with the view — a compile-time guard (LIST_FILTERS_ROUND_TRIP) rejects any future list filter the export
   * contract cannot carry. There is no saved configuration to pick: #499 deleted the template layer, and the
   * column order comes from FieldDefinition.DisplayOrder, the very axis these columns are rendered by.
   *
   * Scope is the current filter set, never a row selection: abp-extensible-table has no row-selection
   * support, so there is nothing to check. Over-limit is a server-side fail-fast (no truncated file); its
   * message arrives inside a Blob body and is surfaced by readBlobErrorMessage.
   */
  exportCurrentView(format: ExportFormat): void {
    const documentTypeCode = this.typeFilter();
    if (!documentTypeCode || this.isExporting()) return;
    this.isExporting.set(true);

    this.documentExportService
      // skipHandleError: ABP's global handler assumes a JSON error body and throws
      // "Cannot read properties of undefined (reading 'details')" on this responseType:'blob' request
      // before it can show anything. Opt out and own the error below.
      .export(toExportDocumentsInput(documentTypeCode, format, this.buildFilter()), { skipHandleError: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          // The reset lives in `finally`: RxJS routes a throw inside a `next` handler to
          // reportUnhandledError, never to the `error` handler below. So if triggerBlobDownload throws — a
          // CSP-blocked or sandboxed `blob:` URL, an overridden `URL` — `isExporting` would stay true and the
          // toolbar button would stay disabled, silently, until a page reload.
          try {
            triggerBlobDownload(
              blob,
              exportFileName(documentTypeCode, format === ExportFormat.Xlsx, new Date()),
            );
          } finally {
            this.isExporting.set(false);
          }
        },
        error: (err: unknown) => {
          this.isExporting.set(false);
          void readBlobErrorMessage(err).then(message =>
            this.toaster.error(
              message ? escapeHtmlChars(message) : '::Document:Export:Failed',
              '::Error',
            ),
          );
        },
      });
  }

  // The list carries only documentTypeCode as the output contract. Map it to the displayName of a type
  // visible in the current layer; fall back to code when cross-layer or deleted types cannot be resolved.
  // cabinets() remains available for the top filter dropdown.
  documentTypeDisplayName(code: string | null | undefined): string | null {
    if (!code) return null;
    return this.documentTypes.value().find(t => t.typeCode === code)?.displayName ?? code;
  }

  // Load the selected type's field definitions and turn them into dynamic columns
  // (ordered by displayOrder). Cleared when no single type is selected. Errors fall
  // back to no columns rather than breaking the list (mirrors loadDocumentTypes).
  // The type filter is keyed by typeCode (Document exit contract); the field-definition
  // API is keyed by immutable DocumentTypeId (#207), so we resolve code → id via the
  // already-loaded visible types before querying.
  private loadExtractedFieldColumns(typeCode: string): void {
    const documentTypeId = this.documentTypes.value().find(t => t.typeCode === typeCode)?.id;
    if (!documentTypeId) {
      if (this.typeFilter() === typeCode) {
        this.applyExtractedFieldColumns([]);
      }
      return;
    }
    this.fieldDefinitionService.getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: fields => {
          if (this.typeFilter() !== typeCode) return;
          this.applyExtractedFieldColumns(
            [...fields].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)),
          );
        },
        error: () => {
          if (this.typeFilter() !== typeCode) return;
          this.applyExtractedFieldColumns([]);
        },
      });
  }

  // Entry point for a freshly (re)loaded set of the current type's field definitions. Stores them
  // unfiltered (so a later-landing indexableByFieldType can re-derive the same result) and narrows to
  // isFilterableField before they become columns — the same predicate FieldValueFilterComponent already
  // uses to build its picker, so "shown in the list" and "filterable" stay one decision, not two.
  private applyExtractedFieldColumns(fields: FieldDefinitionDto[]): void {
    this.rawExtractedFields = fields;
    this.updateExtractedFieldColumns(
      fields.filter(f => isFilterableField(f.fieldTypeName, f.isSearchable, this.indexableByFieldType)),
    );
  }

  private updateExtractedFieldColumns(fields: FieldDefinitionDto[]): void {
    this.extractedFieldColumns.set(fields);
    this.rebuildTableProps(fields);
  }

  onTableActivate(event: TableActivateEvent): void {
    if (this.isBulkDeleting() || event.type !== 'click' || !event.row) return;
    const target = event.event?.target as HTMLElement | null;
    if (target?.closest?.('input[type="checkbox"]')) return;
    this.openDetail(event.row);
  }

  // #632: the bulk-delete selection is narrowed to rows the caller may actually delete. abp-extensible-table
  // exposes selection as one table-wide `selectable` flag and forwards neither ngx-datatable's `selectCheck`
  // nor `disableRowCheck`, so a per-row checkbox cannot be disabled — the selection is filtered on the way in
  // instead. Because `[selected]` is bound back to this signal, a row the caller may not delete visibly
  // refuses to stay checked (including via the header "select all"), and the bulk bar's count never promises
  // a deletion the server would refuse.
  onSelectionChange(selected: DocumentListItemDto[]): void {
    this.selectedDocuments.set(selected.filter(doc => rightsOf(doc).canDelete));
  }

  clearSelection(): void {
    this.selectedDocuments.set([]);
  }

  openDetail(doc: DocumentListItemDto): void {
    this.router.navigate(['/documents', doc.id]);
  }

  uploadNew(): void {
    this.router.navigate(['/documents']);
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
              this.clearSelection();
              this.list.getWithoutPageReset();
              // A deleted document leaves the (soft-delete-filtered) review queue — refresh the badge.
              this.loadReviewQueueCount();
            },
            error: () => this.toaster.error('::Document:DeleteFailed', '::Error'),
          });
        }
      });
  }

  bulkDelete(): void {
    // A selected bundle can depend on selected sub-documents. Process non-containers first and
    // serialize the requests so the existing HasSubDocuments guard sees those children removed.
    const selected = [...this.selectedDocuments()]
      .sort((left, right) => Number(!!left.isContainer) - Number(!!right.isContainer));
    if (selected.length === 0 || this.isBulkDeleting()) return;

    this.confirmation
      .warn('::Document:Bulk:DeleteConfirm', '::AreYouSure', {
        yesText: '::Document:Bulk:Delete',
        messageLocalizationParams: [String(selected.length)],
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;

        this.isBulkDeleting.set(true);
        executeBulkOperations(selected, doc => this.documentService.delete(doc.id!), 1)
          .pipe(
            finalize(() => this.isBulkDeleting.set(false)),
            takeUntilDestroyed(this.destroyRef),
          )
          .subscribe(result => {
            const succeeded = result.succeeded.length;
            const failed = result.failed.length;
            if (failed === 0) {
              this.toaster.success('::Document:Bulk:DeleteSucceeded', '::Success', {
                messageLocalizationParams: [String(succeeded)],
              });
            } else if (succeeded > 0) {
              this.toaster.warn('::Document:Bulk:DeletePartial', 'AbpUi::Warning', {
                messageLocalizationParams: [String(succeeded), String(failed)],
              });
            } else {
              this.toaster.error('::Document:Bulk:DeleteFailed', '::Error', {
                messageLocalizationParams: [String(failed)],
              });
            }

            this.clearSelection();
            this.list.getWithoutPageReset();
            this.loadReviewQueueCount();
          });
      });
  }

  private reconcileSelection(items: DocumentListItemDto[]): void {
    const selectedIds = new Set(this.selectedDocuments().map(doc => doc.id).filter(Boolean));
    this.selectedDocuments.set(items.filter(doc => !!doc.id && selectedIds.has(doc.id)));
  }

  // Canonical needs-review total for the toolbar badge. Gated on canReviewAnyType so non-reviewers (who
  // don't see the gateway button) never fire the call. The statistics endpoint shares the review queue's
  // RequiresAttention predicate (#333), so the badge and the queue never drift.
  // #632: additionally gated on Documents.ReadAll. These are whole-layer aggregates and the endpoint now
  // requires ReadAll, so a caller narrowed to a few types gets the toggle but no badge rather than a 403.
  // #632 code review: the second half of this gate, canReviewAnyType(), reads the documentTypes signal, so
  // it cannot answer anything but "module-wide only" until getVisible has resolved. ngOnInit used to call
  // this synchronously ahead of that fetch, so a caller holding Documents.ReadAll plus per-type Edit grants
  // but no module-wide ConfirmClassification was gated out on an empty type list and never asked again —
  // a permanent 0 on the badge for exactly the persona #632 exists for. The initial call is therefore made
  // from loadDocumentTypes' own handlers; the refresh calls after delete / bulk delete / confirm still come
  // from their own call sites, by which time the types are long since loaded.
  private loadReviewQueueCount(): void {
    if (!this.canReadAll || !this.canReviewAnyType()) return;
    this.statisticsService.get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: stats => this.reviewQueueCount.set(stats.needsReviewCount ?? 0),
        error: () => this.reviewQueueCount.set(0),
      });
  }

  // #284: show the confirm-classification button only when the document still requires attention
  // (requiresReview, with disposition already considered server-side so rejected documents no longer
  // require attention) and classification is unresolved. Missing required fields are completed on the
  // detail page.
  needsConfirmation(doc: DocumentListItemDto): boolean {
    return doc.requiresReview === true &&
      ((doc.reviewReasons ?? DocumentReviewReasons.None) & DocumentReviewReasons.UnresolvedClassification)
        !== DocumentReviewReasons.None;
  }

  // #284: pure availability axis. Removed the old review-mixed judgment; after the two axes became
  // orthogonal, the two badges render independently and are no longer mutually exclusive.
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
    // #632: resolved against the ASSIGNABLE types, not every visible one — a pre-selection the caller may
    // not assign would arm the submit button with a request the server refuses.
    this.selectedTypeId.set(
      this.assignableTypes().find(t => t.typeCode === doc.documentTypeCode)?.id ?? '',
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
        // Confirming a classification clears its review reason — keep the badge in step with the queue.
        this.loadReviewQueueCount();
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
      case DocumentLifecycleStatus.PendingReview:
        return 'badge bg-info text-dark';
      case DocumentLifecycleStatus.Ready:
        return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:
        return 'badge bg-danger';
      default:
        return 'badge bg-secondary';
    }
  }

  // #284: review badge text follows the reason: classification confirmation pending or required fields
  // missing. The client renders only reviewReasons provided by the server.
  reviewBadgeLabel(doc: DocumentListItemDto): string {
    const reasons = doc.reviewReasons ?? DocumentReviewReasons.None;
    if ((reasons & DocumentReviewReasons.UnresolvedClassification) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:UnresolvedClassification';
    }
    if ((reasons & DocumentReviewReasons.DuplicateSuspected) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:DuplicateSuspected';
    }
    if ((reasons & DocumentReviewReasons.MissingRequiredFields) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:MissingRequiredFields';
    }
    if ((reasons & DocumentReviewReasons.FieldExtractionIncomplete) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:FieldExtractionIncomplete';
    }
    if ((reasons & DocumentReviewReasons.FieldValidationWarning) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:FieldValidationWarning';
    }
    if ((reasons & DocumentReviewReasons.SegmentationIncomplete) !== DocumentReviewReasons.None) {
      return '::Document:ReviewReason:SegmentationIncomplete';
    }
    return '::Document:NeedsReview';
  }

  getStatusLabel(status: DocumentLifecycleStatus | undefined): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:
        return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing:
        return '::Document:Status:Processing';
      case DocumentLifecycleStatus.PendingReview:
        return '::Document:Status:PendingReview';
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

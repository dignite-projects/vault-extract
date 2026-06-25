import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  effect,
  inject,
  signal,
  computed,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, of, switchMap, tap } from 'rxjs';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule, Location } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { marked } from 'marked';
import { LocalizationPipe, LocalizationService, PermissionService } from '@abp/ng.core';
import { DynamicFormComponent, type FormFieldConfig } from '@abp/ng.components/dynamic-form';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  CabinetDto,
  CabinetService,
  DocumentDto,
  DocumentLifecycleStatus,
  DocumentPipelineRunDto,
  DocumentPipelineRunService,
  DocumentReviewDisposition,
  DocumentReviewReasons,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  FieldDataType,
  FieldDefinitionDto,
  FieldDefinitionService,
  EXTRACT_PERMISSIONS,
  PipelineRunStatus,
} from '@dignite/vault-extract';
import { formatExtractedFieldValue } from '../../shared/format-field-value';
import { DocumentFileBlobService } from '../../shared/document-file-blob.service';
import { isImageContentType, isPdfContentType } from '../../shared/content-type';

interface PipelineRow {
  pipelineCode: string;
  labelKey: string;
  isKnown: boolean;
  run: DocumentPipelineRunDto | null;
  // Pre-computed view fields. Without these, the template re-invoked
  // getRunStatusBadgeClass / getElapsedMs / formatElapsed / isRetryable on
  // every change detection cycle for every row. Now they are derived once
  // when the pipelineRows signal recomputes (i.e. when the document is
  // (re)loaded).
  statusBadgeClass: string;
  statusLabel: string;
  inProgress: boolean;
  elapsedDisplay: string | null;
  retryable: boolean;
}

// Mirrors core/src/Dignite.Vault.Extract.Domain.Shared/Documents/ExtractPipelines.cs.
const KNOWN_PIPELINE_CODES = [
  'text-extraction',
  'classification',
] as const;

@Component({
  selector: 'lib-document-detail',
  templateUrl: './document-detail.component.html',
  styleUrls: ['./document-detail.component.scss'],
  imports: [CommonModule, FormsModule, DynamicFormComponent, LocalizationPipe],
  providers: [DocumentFileBlobService],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly documentService = inject(DocumentService);
  private readonly documentPipelineRunService = inject(DocumentPipelineRunService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly cabinetService = inject(CabinetService);
  private readonly toaster = inject(ToasterService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly permissionService = inject(PermissionService);
  private readonly localization = inject(LocalizationService);
  private readonly destroyRef = inject(DestroyRef);
  // Original-file blob load / sanitize / revoke lifecycle (#277), shared with the file-preview page.
  protected readonly fileBlob = inject(DocumentFileBlobService);

  readonly canDelete = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Delete,
  );
  readonly canEditFields = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Default,
  );

  document = signal<DocumentDto | null>(null);
  // #306/#354: when this document is a sub-document (originDocumentId set), the source/container document
  // loaded for the provenance banner so it can show the parent's title instead of a raw id. null when this
  // is a normal document, or when the parent is inaccessible (soft-deleted / cross-layer) — the banner then
  // falls back to the id.
  parentDocument = signal<DocumentDto | null>(null);
  // #306/#354: true when the parent (origin) lookup failed — the source was removed (soft / permanently deleted) or is
  // cross-layer / inaccessible. Drives the banner's "source unavailable" label and hides the dead "view parent" link,
  // distinguishing a failed lookup from the brief in-flight window (both otherwise leave parentDocument null).
  parentLookupFailed = signal(false);
  // #216: PipelineRun was split into an independent aggregate root and removed from
  // DocumentDto.pipelineRuns. It is now an independent signal loaded separately through
  // DocumentPipelineRunService in loadDocument.
  pipelineRuns = signal<DocumentPipelineRunDto[]>([]);
  isLoading = signal(true);
  // Left column three-tab area (#274): Markdown preview by default; switching to 'file' triggers lazy
  // loading of the original-file blob.
  activeTab = signal<'preview' | 'source' | 'file'>('preview');
  retryingPipeline = signal<string | null>(null);
  isRerecognizing = signal(false);
  isEditingFields = signal(false);
  isSavingFields = signal(false);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  extractedFieldFormFields = signal<FormFieldConfig[]>([]);
  // Candidate cabinets for the document: cabinetId-to-name display mapping plus reassignment dropdown
  // options (#257). Loaded only with Cabinets.Default permission.
  cabinets = signal<CabinetDto[]>([]);
  // Cabinet reassignment (#257) edit state: entering edit shows the dropdown; empty selectedCabinetId
  // means unclassified.
  isEditingCabinet = signal(false);
  isSavingCabinet = signal(false);
  selectedCabinetId = signal<string>('');
  // Document types visible in the current layer, used for typeCode-to-displayName mapping and the
  // confirm-classification picker. Populated together with field definition loading.
  documentTypes = signal<DocumentTypeDto[]>([]);

  // #395: manual confirm/assign classification — the authoritative override for UnresolvedClassification,
  // relocated here from the removed review-queue page.
  showClassifyDialog = signal(false);
  selectedTypeId = signal('');
  isConfirmingClassification = signal(false);

  // #395: reject (#237/#284 recoverable disposition). The review queue was the only place this action
  // lived; it moves to the remediation hub so the disposition is not lost.
  showRejectDialog = signal(false);
  rejectReason = signal('');
  isRejecting = signal(false);
  // #411: in-flight guard for the "Allow duplicate" operator action.
  isAllowingDuplicate = signal(false);

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewDisposition = DocumentReviewDisposition;
  readonly DocumentReviewReasons = DocumentReviewReasons;
  readonly PipelineRunStatus = PipelineRunStatus;

  pipelineRows = computed<PipelineRow[]>(() => {
    if (!this.document()) return [];

    // #216: run source changed from doc.pipelineRuns to the independent pipelineRuns signal
    // (DocumentPipelineRunService).
    const allRuns = this.pipelineRuns();
    const known: PipelineRow[] = KNOWN_PIPELINE_CODES.map(code => this.toPipelineRow(
      code,
      `::Document:Pipeline:${code}`,
      true,
      this.pickLatestRun(allRuns, code),
    ));

    const unknownCodes = Array.from(
      new Set(
        allRuns
          .map(r => r.pipelineCode)
          .filter((code): code is string => !!code && !KNOWN_PIPELINE_CODES.includes(code as typeof KNOWN_PIPELINE_CODES[number]))
      )
    );

    const unknown: PipelineRow[] = unknownCodes.map(code => this.toPipelineRow(
      code,
      code,
      false,
      this.pickLatestRun(allRuns, code),
    ));

    return [...known, ...unknown];
  });

  protected toPipelineRow(
    pipelineCode: string,
    labelKey: string,
    isKnown: boolean,
    run: DocumentPipelineRunDto | null,
  ): PipelineRow {
    return {
      pipelineCode,
      labelKey,
      isKnown,
      run,
      statusBadgeClass: this.getRunStatusBadgeClass(run?.status),
      statusLabel: this.getRunStatusLabel(run?.status),
      inProgress: this.isRunInProgress(run?.status),
      elapsedDisplay: run ? this.formatElapsedOrNull(run) : null,
      retryable: isKnown && this.isRetryable(run),
    };
  }

  protected formatElapsedOrNull(run: DocumentPipelineRunDto): string | null {
    return this.getElapsedMs(run) === null ? null : this.formatElapsed(run);
  }

  // #284: operator attention required, for any unresolved review reason, equals server-provided
  // requiresReview. The client does not infer it locally.
  needsReview = computed(() => this.document()?.requiresReview ?? false);

  // #395: classification still unresolved AND the operator may act — drives the "Confirm classification"
  // CTA. Mirrors the list's needsConfirmation; the blocking UnresolvedClassification reason is the only one
  // a manual type assignment resolves.
  needsClassification = computed(() =>
    this.canEditFields &&
    (((this.document()?.reviewReasons ?? DocumentReviewReasons.None) & DocumentReviewReasons.UnresolvedClassification)
      !== DocumentReviewReasons.None),
  );

  // #411: a suspected duplicate AND the operator may act — drives the "Allow" CTA (release as not a duplicate).
  // The opposite resolution (confirm the duplicate) is the existing Delete action.
  needsDuplicateReview = computed(() =>
    this.canEditFields &&
    (((this.document()?.reviewReasons ?? DocumentReviewReasons.None) & DocumentReviewReasons.DuplicateSuspected)
      !== DocumentReviewReasons.None),
  );

  // #284: pure availability axis. After the two axes became orthogonal, the review banner and processing
  // banner are judged independently; the template's @if needsReview takes precedence over isProcessing.
  isProcessing = computed(() => {
    const status = this.document()?.lifecycleStatus;
    return status === DocumentLifecycleStatus.Uploaded ||
           status === DocumentLifecycleStatus.Processing;
  });

  isReady = computed(() =>
    this.document()?.lifecycleStatus === DocumentLifecycleStatus.Ready
  );

  // #306/#354: this document was derived from a constituent of another (container) document. Drives the
  // provenance banner and its "view parent / view siblings" navigation. originDocumentId is a system signal
  // carried on the Document output contract (DocumentDto), null for normally-uploaded documents.
  isSubDocument = computed(() => !!this.document()?.originDocumentId);

  // True when any critical pipeline, text extraction or classification, has an in-progress run
  // (Pending/Running).
  pipelineInProgress = computed(() =>
    this.pipelineRows().some(r => r.isKnown && r.inProgress)
  );

  // #263 "rerecognize" availability: extracted text exists, ConfirmClassification permission is present
  // (same as canEditFields), no critical pipeline is currently running, and the page is not loading. This
  // avoids stacking reclassification onto a document already being processed or reprocessed.
  // Use pipelineInProgress instead of !isProcessing(): the latter is always false when needsReview() is
  // true, which would still expose the button on a pending-review document while reclassification is in
  // progress (review #5). The in-flight POST is covered by button [disabled]="isRerecognizing()".
  canRerecognize = computed(() =>
    this.canEditFields &&
    !!this.document()?.markdown &&
    !this.pipelineInProgress() &&
    !this.isLoading()
  );

  isReextracting = signal(false);

  // #289 "field re-extraction only" availability: same prerequisites as "rerecognize", plus already
  // classified with documentTypeCode. Field extraction is attached to a type, so unclassified documents
  // have nothing to extract from; this mirrors the backend NotClassified guard.
  canReextractFields = computed(() =>
    this.canEditFields &&
    !!this.document()?.documentTypeCode &&
    !!this.document()?.markdown &&
    !this.pipelineInProgress() &&
    !this.isLoading()
  );

  isImage = computed(() =>
    isImageContentType(this.document()?.fileOrigin?.contentType)
  );

  isPdf = computed(() =>
    isPdfContentType(this.document()?.fileOrigin?.contentType)
  );

  // Sub-documents (#306/#346) are spawned with no FileOrigin — their Markdown is seeded from the source
  // segment slice, so there is no original file to preview or download. Gate every source-file affordance
  // (Original File tab, footer, Download) on this so the UI never calls GetBlobAsync for a blob-less
  // document, which fails with Extract:DocumentNoSourceBlob (and re-fires on every reload after rerecognize
  // / Refresh while the file tab is active).
  hasSourceFile = computed(() => !!this.document()?.fileOrigin);

  // Intermediate computed for Markdown source (#274 review): when document() changes but markdown does
  // not, such as field or cabinet changes, return the same string so downstream renderedMarkdown can
  // short-circuit by value equality and avoid repeated marked.parse calls.
  private markdownSource = computed(() => this.document()?.markdown ?? '');

  // Markdown preview (#274): marked renders to an HTML string. When the template binds [innerHTML],
  // Angular's built-in DomSanitizer sanitizes it automatically by stripping <script>, on*, and
  // javascript:. Never bypassSecurityTrustHtml: Markdown is attacker-influenced content because VLM OCR
  // can be prompt-injected by text inside an image, so the sanitizer must stay on end to end.
  renderedMarkdown = computed<string>(() => {
    const md = this.markdownSource();
    return md ? (marked.parse(md, { gfm: true, async: false }) as string) : '';
  });

  // Owning cabinet name for the document, cabinetId to name. Returns null when unclassified or
  // unresolved because of missing permission or deleted cabinet.
  cabinetName = computed<string | null>(() => {
    const id = this.document()?.cabinetId;
    if (!id) return null;
    return this.cabinets().find(c => c.id === id)?.name ?? null;
  });

  // Document type displayName, mapped from typeCode. Returns null when unclassified, and falls back to
  // code for cross-layer or deleted types.
  documentTypeDisplayName = computed<string | null>(() => {
    const code = this.document()?.documentTypeCode;
    if (!code) return null;
    return this.documentTypes().find(t => t.typeCode === code)?.displayName ?? code;
  });

  // Type-bound extracted fields (field architecture v2). Show only values corresponding to currently
  // active field definitions:
  // The backend ExtractedFields output pierces soft-delete and still includes historical values for
  // deleted field definitions, preserving data for downstream consumers (#206/#207). The operator UI no
  // longer shows them, matching the list's dynamic columns which also use only active definitions. Labels
  // use displayName and sorting uses displayOrder.
  extractedFieldEntries = computed<{ key: string; label: string; value: string }[]>(() => {
    const fields = this.document()?.extractedFields;
    if (!fields) return [];
    const defByName = new Map(this.fieldDefinitions().map(d => [d.name ?? '', d]));
    return Object.keys(fields)
      .filter(key => defByName.has(key))
      .sort((a, b) =>
        (defByName.get(a)!.displayOrder ?? 0) - (defByName.get(b)!.displayOrder ?? 0) ||
        a.localeCompare(b))
      .map(key => ({
        key,
        label: defByName.get(key)!.displayName || key,
        value: this.formatFieldValue(fields[key]),
      }));
  });

  // Field card display condition: existing extracted values for read-only display, or editable with field
  // definitions on this type so empty fields can be completed.
  showFieldsCard = computed(() =>
    this.extractedFieldEntries().length > 0 ||
    (this.canEditFields && this.fieldDefinitions().length > 0)
  );

  private documentId!: string;
  // If the blob has not loaded when Download File is clicked, set this flag and trigger one download
  // after the blob is ready. See downloadFile plus the constructor effect.
  private pendingDownload = false;

  constructor() {
    // Pending download completion: when the blob arrives, trigger one download; when blob loading fails,
    // show a hint. pendingDownload is a plain boolean, and this effect reruns only when fileBlob signals
    // change. The download click first changes signals by triggering loading, so the logic naturally hits
    // only once.
    effect(() => {
      const url = this.fileBlob.blobUrl();
      const failed = this.fileBlob.hasError();
      if (!this.pendingDownload) return;
      if (url) {
        this.pendingDownload = false;
        this.fileBlob.download(this.downloadFileName());
      } else if (failed) {
        this.pendingDownload = false;
        this.toaster.error('::Document:DownloadFailed', '::Error');
      }
    });
  }

  ngOnInit(): void {
    // React to the :id route param rather than reading a one-time snapshot. Navigating between documents
    // that share this route — a sub-document's "view parent"/"view siblings" actions, or any /documents/:id
    // link — reuses this component instance, so ngOnInit does not fire again. Without reacting to the param
    // the URL would change but the loaded document would not. Reload whenever the id actually changes,
    // dropping the previous document's cached file blob and resetting the tab so a stale preview never
    // carries over to the new document.
    this.route.paramMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => {
        const id = params.get('id');
        if (!id || id === this.documentId) return;
        this.documentId = id;
        this.fileBlob.reset();
        this.activeTab.set('preview');
        this.loadDocument();
      });
  }

  refresh(): void {
    this.loadDocument();
  }

  private loadDocument(): void {
    this.isLoading.set(true);
    // doc and runs are independent after #216, so load them once in parallel; fieldDefinitions still
    // depend on doc.documentTypeCode and remain sequential.
    forkJoin({
      doc: this.documentService.get(this.documentId),
      runs: this.documentPipelineRunService.getList(this.documentId),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ doc, runs }) => {
          this.document.set(doc);
          this.pipelineRuns.set(runs);
          this.isLoading.set(false);
          // Original-file blob lazy loading (#274): do not fetch while loading the document by default;
          // download only when the Original File tab is selected. See selectTab.
          // If the user is already on that tab, call ensureFilePreview once after reload
          // (Refresh / rerecognize). It returns early when the blob is cached and resets a previous error
          // for retry, preventing Refresh from being ineffective on a stuck preview (#274 review).
          if (this.activeTab() === 'file') {
            this.ensureFilePreview();
          }
          // Field definitions are used for: 1. displayName in extracted-field display for all viewers;
          // 2. completing empty fields when editable. Backend GetListAsync has been open to
          // Documents.Default since #223, so load whenever a type exists and no longer gate on edit
          // permission. #395: visible types are always loaded (even when unclassified) so the
          // confirm-classification picker has its options.
          this.fieldDefinitions.set([]);
          this.loadDocumentTypesAndFields(doc.documentTypeCode);
          // #306/#354: a sub-document carries its source (container) id. Fetch the parent's lightweight
          // metadata so the provenance banner can show its title; reset first so a previous document's
          // parent never lingers when navigating between documents in the same component instance.
          this.parentDocument.set(null);
          this.parentLookupFailed.set(false);
          if (doc.originDocumentId) {
            this.loadParentDocument(doc.originDocumentId);
          }
          // Cabinet name mapping: fetch only when Cabinets.Default is granted and not already loaded. If
          // there is no permission, the cabinet row is hidden.
          if (this.canViewCabinets && this.cabinets().length === 0) {
            this.loadCabinets();
          }
        },
        error: () => {
          this.isLoading.set(false);
        },
      });
  }

  // doc.documentTypeCode is the current code projection in the Document output contract (#207). The
  // field definition API associates by immutable DocumentTypeId, so first resolve code to id from types
  // visible in the current layer, then query. #395: visible types are always stored (the
  // confirm-classification picker needs them even when the document is unclassified); field definitions
  // are only fetched when the document already has a type.
  private loadDocumentTypesAndFields(typeCode: string | null | undefined): void {
    this.documentTypeService.getVisible()
      .pipe(
        // One getVisible call serves two purposes: store documentTypes for document type displayName
        // mapping + the confirm picker, then resolve typeId for field definition lookup.
        tap(types => this.documentTypes.set(types)),
        switchMap(types => {
          if (!typeCode) return of<FieldDefinitionDto[]>([]);
          const documentTypeId = types.find(t => t.typeCode === typeCode)?.id;
          if (!documentTypeId) return of<FieldDefinitionDto[]>([]);
          return this.fieldDefinitionService.getList({ documentTypeId });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: defs => this.fieldDefinitions.set(
          [...defs].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0) || (a.name ?? '').localeCompare(b.name ?? '')),
        ),
        error: () => this.fieldDefinitions.set([]),
      });
  }

  // Cabinet candidates for name mapping display plus reassignment dropdown (#257). Called only with
  // Cabinets.Default permission.
  private loadCabinets(): void {
    this.cabinetService.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => this.cabinets.set(list),
        error: () => this.cabinets.set([]),
      });
  }

  // #306/#354: load the source (container) document of a sub-document for the provenance banner. A removed
  // (soft / permanently deleted) or cross-layer parent (404 / filtered) is an EXPECTED outcome — a sub-document
  // outlives its source — so pass skipHandleError to suppress ABP's global error popup; the component handles it by
  // flagging parentLookupFailed, and the banner shows a "source unavailable" label. It never blocks the
  // sub-document's own page.
  private loadParentDocument(originDocumentId: string): void {
    this.documentService.get(originDocumentId, { skipHandleError: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: parent => {
          this.parentDocument.set(parent);
          this.parentLookupFailed.set(false);
        },
        error: () => {
          this.parentDocument.set(null);
          this.parentLookupFailed.set(true);
        },
      });
  }

  // Cabinet reassignment (#257): enter edit mode and preselect the current cabinet; empty string means
  // unclassified.
  startEditCabinet(): void {
    this.selectedCabinetId.set(this.document()?.cabinetId ?? '');
    this.isEditingCabinet.set(true);
  }

  cancelEditCabinet(): void {
    this.isEditingCabinet.set(false);
  }

  saveCabinet(): void {
    const doc = this.document();
    if (!doc) return;
    this.isSavingCabinet.set(true);
    // Empty string becomes null, meaning removed from cabinet / unclassified. Backend validates cabinet
    // existence and current-layer ownership.
    const cabinetId = this.selectedCabinetId() || null;
    this.documentService.updateCabinet(doc.id!, { cabinetId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.document.set(updated);
          this.isSavingCabinet.set(false);
          this.isEditingCabinet.set(false);
          this.toaster.success('::Document:CabinetUpdated', '::Success');
        },
        error: () => {
          this.isSavingCabinet.set(false);
          this.toaster.error('::Document:UpdateFailed', '::Error');
        },
      });
  }

  // Unified entry point for the Original File tab (#274 review): reset the previous preview error first
  // so <img> rebuilds and retries, and failed downloads can refetch. Then ensure the blob is ready; the
  // service prevents duplicate requests and revokes on component destroy.
  // Fixes imageError getting stuck and Refresh being ineffective.
  private ensureFilePreview(): void {
    // A blob-less sub-document has no original file: never call getBlob for it (would throw
    // Extract:DocumentNoSourceBlob). The Original File tab is hidden for it, but loadDocument still reaches
    // here when the tab was active, so guard at the source — this is the path that re-fired on every reload.
    if (!this.hasSourceFile()) return;
    this.fileBlob.resetError();
    this.fileBlob.ensureLoaded(this.documentId);
  }

  // Tab switch (#274): when switching to Original File, ensure the original-file preview is ready.
  selectTab(tab: 'preview' | 'source' | 'file'): void {
    this.activeTab.set(tab);
    if (tab === 'file') {
      this.ensureFilePreview();
    }
  }

  // Download File footer action: trigger browser download of the original file directly, avoiding an
  // extra trip through the preview page.
  // If the blob is cached, from viewing Original File or a previous download, trigger immediately. If not
  // cached, set pendingDownload and reuse the service fetch, which prevents duplicate requests. Blob
  // arrival or failure is completed by the constructor effect.
  downloadFile(): void {
    // Defensive: the Download action is hidden for blob-less sub-documents; never trigger a getBlob for one.
    if (!this.hasSourceFile()) return;
    if (this.fileBlob.blobUrl()) {
      this.fileBlob.download(this.downloadFileName());
      return;
    }
    this.pendingDownload = true;
    this.fileBlob.ensureLoaded(this.documentId);
  }

  private downloadFileName(): string {
    return this.document()?.fileOrigin?.originalFileName || this.document()?.title || 'document';
  }

  // <img> render failure, where the blob downloaded but decode failed, is folded into the unified preview
  // error signal.
  onPreviewError(): void {
    this.fileBlob.markError();
  }

  goBack(): void {
    // Return to wherever the operator came from — the review queue, the list, a cabinet-filtered view, etc.
    // — instead of always the list. The Angular Router stamps an incrementing navigationId on history.state;
    // it is > 1 once any in-app navigation has happened, and 1 on a direct deep-link / refresh (no in-app
    // history to pop). Fall back to the list in that case so Back never leaves the app.
    const navId = (this.location.getState() as { navigationId?: number } | null)?.navigationId;
    if (navId && navId > 1) {
      this.location.back();
    } else {
      this.router.navigate(['/documents/list']);
    }
  }

  // #354: open this sub-document's source (container) document.
  openParentDocument(): void {
    const originDocumentId = this.document()?.originDocumentId;
    if (!originDocumentId) return;
    this.router.navigate(['/documents', originDocumentId]);
  }

  // #411: open a suspected-duplicate candidate so the operator can compare it before allowing / discarding.
  openDocument(documentId: string): void {
    this.router.navigate(['/documents', documentId]);
  }

  // #354: list the sibling sub-documents (all those derived from the same source, including this one),
  // reusing the list's originDocumentId provenance filter via a deep-link query param.
  viewSiblingDocuments(): void {
    const originDocumentId = this.document()?.originDocumentId;
    if (!originDocumentId) return;
    this.router.navigate(['/documents/list'], { queryParams: { originDocumentId } });
  }

  delete(): void {
    const doc = this.document();
    if (!doc) return;
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.delete(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
          next: () => {
            this.toaster.success('::Document:DeletedSuccessfully', '::Success');
            this.router.navigate(['/documents/list']);
          },
        });
      });
  }

  // #263 "rerecognize": rerun AI automatic classification on the existing Markdown, cascading to field
  // re-extraction without rerunning OCR.
  // This is an overwriting operation, replacing current type and manually edited field values, so confirm
  // first. Reload after success to reflect Processing state.
  rerecognize(): void {
    const doc = this.document();
    if (!doc || this.isRerecognizing()) return;
    this.confirmation
      .warn('::Document:Rerecognize:Confirm', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.isRerecognizing.set(true);
        this.documentService.rerecognize(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.isRerecognizing.set(false);
              this.toaster.success('::Document:RerecognizeQueued', '::Success');
              this.loadDocument();
            },
            error: () => {
              this.isRerecognizing.set(false);
              this.toaster.error('::Document:RerecognizeFailed', '::Error');
            },
          });
      });
  }

  // #289 "field re-extraction only": rerun only the field-extraction pipeline on the existing
  // classification, without reclassification or OCR.
  // Safe leaf operation that overwrites only field values, including manual corrections. Confirm first.
  // Lifecycle-neutral: does not move already Ready documents backward.
  reextractFields(): void {
    const doc = this.document();
    if (!doc || this.isReextracting()) return;
    this.confirmation
      .warn('::Document:ReextractFields:Confirm', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.isReextracting.set(true);
        this.documentService.reextractFields(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.isReextracting.set(false);
              this.toaster.success('::Document:ReextractFieldsQueued', '::Success');
              this.loadDocument();
            },
            error: () => {
              this.isReextracting.set(false);
              this.toaster.error('::Document:ReextractFieldsFailed', '::Error');
            },
          });
      });
  }

  // #395: manual confirm / assign document type. Authoritative override that clears
  // UnresolvedClassification and cascades field extraction (backend confirmClassification). Defaults the
  // picker to the document's current low-confidence type when present so the operator usually just confirms.
  openClassifyDialog(): void {
    const doc = this.document();
    if (!doc) return;
    this.selectedTypeId.set(
      this.documentTypes().find(t => t.typeCode === doc.documentTypeCode)?.id ?? '',
    );
    this.showClassifyDialog.set(true);
  }

  closeClassifyDialog(): void {
    this.showClassifyDialog.set(false);
    this.selectedTypeId.set('');
  }

  submitClassify(): void {
    const doc = this.document();
    if (!doc || !this.selectedTypeId() || this.isConfirmingClassification()) return;
    this.isConfirmingClassification.set(true);
    this.documentService.confirmClassification(doc.id!, { documentTypeId: this.selectedTypeId() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isConfirmingClassification.set(false);
          this.closeClassifyDialog();
          this.toaster.success('::Document:ClassificationConfirmed', '::Success');
          this.loadDocument();
        },
        error: () => {
          this.isConfirmingClassification.set(false);
          this.toaster.error('::Document:ConfirmFailed', '::Error');
        },
      });
  }

  // #395: reject the document (#237/#284 recoverable disposition). The rejection reason is required by the
  // backend RejectReviewInput.Reason; reload after success so disposition/badge reflect the new state.
  openRejectDialog(): void {
    if (!this.document()) return;
    this.rejectReason.set('');
    this.showRejectDialog.set(true);
  }

  closeRejectDialog(): void {
    this.showRejectDialog.set(false);
    this.rejectReason.set('');
  }

  submitReject(): void {
    const doc = this.document();
    if (!doc || this.isRejecting()) return;
    const reason = this.rejectReason().trim();
    if (!reason) {
      this.toaster.warn('::Document:Review:RejectReasonRequired');
      return;
    }
    this.isRejecting.set(true);
    this.documentService.rejectReview(doc.id!, { reason })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isRejecting.set(false);
          this.closeRejectDialog();
          this.toaster.success('::Document:Review:RejectedSuccessfully', '::Success');
          this.loadDocument();
        },
        error: () => {
          this.isRejecting.set(false);
          this.toaster.error('::Document:Review:ActionFailed', '::Error');
        },
      });
  }

  // #411: operator decides a suspected duplicate is not a duplicate (or is an acceptable re-upload). The backend
  // sets the durable DuplicateAllowed override, clears the blocking reason, and re-derives lifecycle (releasing the
  // document to Ready + DocumentReadyEto when nothing else blocks). Reload so the badge / banner reflect the change.
  allowDuplicate(): void {
    const doc = this.document();
    if (!doc || this.isAllowingDuplicate()) return;
    this.isAllowingDuplicate.set(true);
    this.documentService.allowDuplicate(doc.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isAllowingDuplicate.set(false);
          this.toaster.success('::Document:Review:DuplicateAllowed', '::Success');
          this.loadDocument();
        },
        error: () => {
          this.isAllowingDuplicate.set(false);
          this.toaster.error('::Document:Review:ActionFailed', '::Error');
        },
      });
  }

  getStatusBadgeClass(status: DocumentLifecycleStatus | undefined): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return 'badge bg-secondary';
      case DocumentLifecycleStatus.Processing: return 'badge bg-warning text-dark';
      case DocumentLifecycleStatus.Ready:      return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:     return 'badge bg-danger';
      default:                                 return 'badge bg-secondary';
    }
  }

  // #284: header badge shows only lifecycle on the availability axis. Review state is expressed by the
  // banner plus detail-area reviewReasonDetails and is not duplicated.
  getDocumentStatusBadgeClass(doc: DocumentDto): string {
    return this.getStatusBadgeClass(doc.lifecycleStatus);
  }

  getStatusLabel(status: DocumentLifecycleStatus | undefined): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing: return '::Document:Status:Processing';
      case DocumentLifecycleStatus.Ready:      return '::Document:Status:Ready';
      case DocumentLifecycleStatus.Failed:     return '::Document:Status:Failed';
      default:                                 return '::Document:Status:Unknown';
    }
  }

  getDocumentStatusLabel(doc: DocumentDto): string {
    return this.getStatusLabel(doc.lifecycleStatus);
  }

  // #284: review reason to localization key, used for detail-area reviewReasonDetails rendering.
  reviewReasonLabel(reason: DocumentReviewReasons | undefined): string {
    switch (reason) {
      case DocumentReviewReasons.UnresolvedClassification:
        return '::Document:ReviewReason:UnresolvedClassification';
      case DocumentReviewReasons.MissingRequiredFields:
        return '::Document:ReviewReason:MissingRequiredFields';
      case DocumentReviewReasons.SegmentationIncomplete:
        return '::Document:ReviewReason:SegmentationIncomplete';
      case DocumentReviewReasons.DuplicateSuspected:
        return '::Document:ReviewReason:DuplicateSuspected';
      default:
        return '::Document:NeedsReview';
    }
  }

  getRunStatusBadgeClass(status: PipelineRunStatus | undefined): string {
    switch (status) {
      case PipelineRunStatus.Pending:   return 'badge bg-secondary';
      case PipelineRunStatus.Running:   return 'badge bg-warning text-dark';
      case PipelineRunStatus.Succeeded: return 'badge bg-success';
      case PipelineRunStatus.Failed:    return 'badge bg-danger';
      case PipelineRunStatus.Skipped:   return 'badge bg-light text-dark border';
      default:                          return 'badge bg-light text-muted border';
    }
  }

  getRunStatusLabel(status: PipelineRunStatus | undefined): string {
    switch (status) {
      case PipelineRunStatus.Pending:   return '::Document:Pipeline:Status:Pending';
      case PipelineRunStatus.Running:   return '::Document:Pipeline:Status:Running';
      case PipelineRunStatus.Succeeded: return '::Document:Pipeline:Status:Succeeded';
      case PipelineRunStatus.Failed:    return '::Document:Pipeline:Status:Failed';
      case PipelineRunStatus.Skipped:   return '::Document:Pipeline:Status:Skipped';
      default:                          return '::Document:Pipeline:Status:NotStarted';
    }
  }

  isRunInProgress(status: PipelineRunStatus | undefined): boolean {
    return status === PipelineRunStatus.Pending || status === PipelineRunStatus.Running;
  }

  isRetryable(run: DocumentPipelineRunDto | null | undefined): boolean {
    return !!run && run.status === PipelineRunStatus.Failed;
  }

  retryPipeline(pipelineCode: string): void {
    if (this.retryingPipeline() !== null) return;

    this.retryingPipeline.set(pipelineCode);
    this.documentService.retryPipeline(this.documentId, { pipelineCode })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: () => {
        this.retryingPipeline.set(null);
        this.toaster.success('::Document:Pipeline:RetryQueued', '::Success');
        this.loadDocument();
      },
      error: () => {
        this.retryingPipeline.set(null);
        this.toaster.error('::Document:Pipeline:RetryFailed', '::Error');
      },
    });
  }

  getElapsedMs(run: DocumentPipelineRunDto): number | null {
    if (!run.startedAt) return null;
    const start = new Date(run.startedAt).getTime();
    if (Number.isNaN(start)) return null;
    const end = run.completedAt ? new Date(run.completedAt).getTime() : Date.now();
    if (Number.isNaN(end) || end < start) return null;
    return end - start;
  }

  formatFieldValue(value: unknown): string {
    return formatExtractedFieldValue(value);
  }

  startEditFields(): void {
    this.extractedFieldFormFields.set(this.createExtractedFieldFormFields());
    this.isEditingFields.set(true);
  }

  cancelEditFields(): void {
    this.isEditingFields.set(false);
    this.extractedFieldFormFields.set([]);
  }

  saveFields(formValue: Record<string, unknown>): void {
    const doc = this.document();
    if (!doc) return;
    this.isSavingFields.set(true);

    const fields: Record<string, unknown> = {};
    for (const def of this.fieldDefinitions()) {
      const key = def.name ?? '';
      const value = formValue[key];

      if (this.shouldOmitFieldValue(value)) continue;
      fields[key] = this.coerceValue(def, value);
    }

    this.documentService.updateExtractedFields(doc.id!, { fields })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.document.set(updated);
          this.isSavingFields.set(false);
          this.isEditingFields.set(false);
          this.extractedFieldFormFields.set([]);
          this.toaster.success('::Document:FieldsUpdated', '::Success');
        },
        error: () => {
          this.isSavingFields.set(false);
          this.toaster.error('::Document:UpdateFailed', '::Error');
        },
      });
  }

  private createExtractedFieldFormFields(): FormFieldConfig[] {
    const values = this.document()?.extractedFields ?? {};

    return this.fieldDefinitions().map(def => {
      const config: FormFieldConfig = {
        key: def.name ?? '',
        label: `${def.displayName} (${def.name})`,
        // Multi-value fields (#212, text only) use a textarea with one value per line; single-value fields
        // choose input type by DataType.
        type: def.allowMultiple ? 'textarea' : this.toFormFieldType(def.dataType),
        value: this.toFormInitialValue(def, values[def.name ?? '']),
        required: def.isRequired,
        order: def.displayOrder,
        gridSize: 12,
        validators: def.isRequired
          ? [{ type: 'required', message: '::FieldDefinition:Required' }]
          : [],
      };

      if (def.allowMultiple) {
        config.placeholder = this.localization.instant('::FieldDefinition:AllowMultipleEditHint');
      } else if (def.dataType === FieldDataType.Number) {
        config.step = 'any';
      } else if (def.dataType === FieldDataType.Boolean) {
        config.options = {
          defaultValues: [
            { key: 'true', value: 'true' },
            { key: 'false', value: 'false' },
          ],
        };
      }

      return config;
    });
  }

  private toFormFieldType(dataType: FieldDataType | undefined): FormFieldConfig['type'] {
    switch (dataType) {
      // Long text, such as summaries or descriptions, uses a multiline editor. The default branch of
      // toFormInitialValue already fills it back as a string unchanged.
      case FieldDataType.LongText:
        return 'textarea';
      case FieldDataType.Number:
        return 'number';
      case FieldDataType.Boolean:
        return 'select';
      case FieldDataType.Date:
        return 'date';
      case FieldDataType.DateTime:
        return 'datetime-local';
      default:
        return 'text';
    }
  }

  private toFormInitialValue(def: FieldDefinitionDto, value: unknown): unknown {
    // Multi-value fields (#212): output array becomes one textarea line per value. Non-arrays, including
    // null or unextracted values, become empty.
    if (def.allowMultiple) {
      return Array.isArray(value) ? value.map(v => String(v)).join('\n') : '';
    }

    if (value === null || value === undefined) return '';

    switch (def.dataType) {
      case FieldDataType.Number:
        return this.toNumberInputValue(value);
      case FieldDataType.Boolean:
        return this.parseBoolean(value) ? 'true' : 'false';
      case FieldDataType.Date:
        return this.toDateInputValue(value);
      case FieldDataType.DateTime:
        return this.toDateTimeLocalInputValue(value);
      default:
        return typeof value === 'object' ? JSON.stringify(value) : String(value);
    }
  }

  private shouldOmitFieldValue(value: unknown): boolean {
    return value === null ||
      value === undefined ||
      (typeof value === 'string' && value.trim() === '');
  }

  // Convert to the corresponding JSON type by field DataType. Date/DateTime/Text are always stored as
  // strings.
  private coerceValue(def: FieldDefinitionDto, value: unknown): unknown {
    // Multi-value fields (#212): textarea one value per line, trimmed and with empty lines removed,
    // becomes string[], symmetric with backend UpdateExtractedFieldsAsync receiving arrays.
    if (def.allowMultiple) {
      return String(value ?? '')
        .split(/\r?\n/)
        .map(s => s.trim())
        .filter(s => s.length > 0);
    }

    switch (def.dataType) {
      case FieldDataType.Number: {
        const n = typeof value === 'number' ? value : Number(value);
        return !Number.isNaN(n) ? n : value;
      }
      case FieldDataType.Boolean:
        return this.parseBoolean(value);
      default:
        return value;
    }
  }

  private toNumberInputValue(value: unknown): string {
    const raw = String(value).trim();
    if (raw === '') return '';
    const n = Number(raw);
    return Number.isNaN(n) ? '' : raw;
  }

  private parseBoolean(value: unknown): boolean {
    if (typeof value === 'boolean') return value;
    if (typeof value === 'number') return value !== 0;
    const normalized = String(value).trim().toLowerCase();
    return normalized === 'true' || normalized === '1' || normalized === 'yes';
  }

  private toDateInputValue(value: unknown): string {
    const raw = String(value);
    return /^\d{4}-\d{2}-\d{2}/.test(raw) ? raw.slice(0, 10) : raw;
  }

  private toDateTimeLocalInputValue(value: unknown): string {
    const raw = String(value);
    if (!raw) return '';
    if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(raw)) {
      return raw.slice(0, 16);
    }

    const parsed = new Date(raw);
    if (Number.isNaN(parsed.getTime())) return raw;

    const pad = (n: number) => String(n).padStart(2, '0');
    return `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}` +
      `T${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`;
  }

  formatElapsed(run: DocumentPipelineRunDto): string {
    const ms = this.getElapsedMs(run);
    if (ms == null) return '';
    if (ms < 1000) return `${ms} ms`;
    const seconds = ms / 1000;
    if (seconds < 60) return `${seconds.toFixed(1)} s`;
    const minutes = Math.floor(seconds / 60);
    const remSeconds = Math.round(seconds - minutes * 60);
    return `${minutes}m ${remSeconds}s`;
  }

  protected pickLatestRun(runs: DocumentPipelineRunDto[], pipelineCode: string): DocumentPipelineRunDto | null {
    const matches = runs.filter(r => r.pipelineCode === pipelineCode);
    if (matches.length === 0) return null;
    return matches.reduce((prev, curr) => ((curr.attemptNumber ?? 0) > (prev.attemptNumber ?? 0) ? curr : prev));
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { escapeHtmlChars, ListService, LocalizationPipe, LocalizationService, PermissionService } from '@abp/ng.core';
import type { ABP } from '@abp/ng.core';
import {
  EntityProp,
  EXTENSIONS_IDENTIFIER,
  ExtensionsService,
  ExtensibleTableComponent,
  ePropType,
} from '@abp/ng.components/extensible';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { ResourcePermissionManagementComponent } from '@abp/ng.permission-management';
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { map, of } from 'rxjs';
import {
  CreateDocumentTypeDto,
  DocumentTypeDto,
  DocumentTypePackDto,
  DocumentTypePackService,
  DocumentTypeService,
  DuplicateDetectionScope,
  DuplicateScopePreviewDto,
  EXTRACT_PERMISSIONS,
  SlugSuggestionService,
} from '@dignite/ng.vault-extract';
import {
  ClientPagedResult,
  configureEntityTable,
  pageClientItems,
  EXTRACT_TABLES,
  SortAccessors,
} from '../../shared/extensible-table';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';
import { DocumentTypesStore } from '../../shared/document-types.store';
import { FieldReextractionModalComponent } from '../../reprocessing/field-reextraction-modal/field-reextraction-modal.component';
import { ReclassificationModalComponent } from '../../reprocessing/reclassification-modal/reclassification-modal.component';
import { DocumentTypePackImportModalComponent } from '../packs/document-type-pack-import-modal.component';
import { packFileName, serializePacks } from '../packs/pack-io';
import { triggerBlobDownload } from '../../shared/blob-download';

// Mirrors DocumentTypeConsts (Domain.Shared): TypeCode whitelist + length cap.
const TYPE_CODE_PATTERN = /^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$/;
const MAX_TYPE_CODE_LENGTH = 128;
const MAX_DISPLAY_NAME_LENGTH = 128;
const MAX_DESCRIPTION_LENGTH = 512;

const DOCUMENT_TYPE_SORTS: SortAccessors<DocumentTypeDto> = {
  typeCode: type => type.typeCode,
  displayName: type => type.displayName,
  confidenceThreshold: type => type.confidenceThreshold,
  priority: type => type.priority,
  duplicateScope: type => type.duplicateScope ?? DuplicateDetectionScope.Layer,
};

// Raw shape of `form.getRawValue()` — named so the #651 preview/save helpers below can pass it around
// without inlining the form's control shape at every call site.
interface DocumentTypeFormValue {
  typeCode: string;
  displayName: string;
  description: string;
  confidenceThreshold: number;
  priority: number;
  duplicateScope: DuplicateDetectionScope;
}

@Component({
  selector: 'lib-document-type-list',
  templateUrl: './document-type-list.component.html',
  styleUrls: ['./document-type-list.component.scss'],
  imports: [
    CommonModule,
    RouterModule,
    ReactiveFormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
    FieldReextractionModalComponent,
    ReclassificationModalComponent,
    DocumentTypePackImportModalComponent,
    ResourcePermissionManagementComponent,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: EXTRACT_TABLES.DocumentTypes,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentTypeListComponent implements OnInit {
  private readonly service = inject(DocumentTypeService);
  private readonly packService = inject(DocumentTypePackService);
  private readonly slugService = inject(SlugSuggestionService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);
  // #635: this page is the only place in the SPA that WRITES the document-type layer, so it is the
  // invalidation site for the shared store the reading pages hold. See onDocumentTypesChanged.
  private readonly documentTypes = inject(DocumentTypesStore);

  readonly list = inject(ListService);

  // Create/edit/delete buttons require any DocumentTypes write grant (#217); the route's
  // DocumentTypes.Default only lists. ABP evaluates the `||` policy expression.
  readonly canManage = this.permissionService.getGrantedPolicy(
    `${EXTRACT_PERMISSIONS.DocumentTypes.Create} || ${EXTRACT_PERMISSIONS.DocumentTypes.Update} || ${EXTRACT_PERMISSIONS.DocumentTypes.Delete}`,
  );

  // Config-pack import (#444): private-deployment migration of type + field config. Mirrors the server gate —
  // import can create both types AND fields, so both Create grants are required to even open it (the server
  // additionally asserts the Update permissions lazily on the branches that update existing rows).
  readonly canImport = this.permissionService.getGrantedPolicy(
    `${EXTRACT_PERMISSIONS.DocumentTypes.Create} && ${EXTRACT_PERMISSIONS.FieldDefinitions.Create}`,
  );

  // Bulk reprocessing entry points (#289): admin-level and independent from type CRUD permissions.
  readonly canReextractFields = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Reprocessing.FieldExtraction,
  );
  readonly canReclassify = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Reprocessing.Reclassification,
  );

  // Per-document-type upload permission dialog (#629): ABP's own resource-permission dialog,
  // its endpoints and its user/role search — nothing here is Vault Extract's. Gated server-side
  // by ManagePermissions, deliberately separate from the schema-editing DocumentTypes.Update.
  readonly canManagePermissions = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.DocumentTypes.ManagePermissions,
  );
  readonly RESOURCE_NAME = EXTRACT_PERMISSIONS.DocumentTypes.Resources.Name;

  // #651: exposed so the template can bind <option [ngValue]="...">, matching the DocumentLifecycleStatus
  // filter select in document-list.component.ts. A plain `[value]` would coerce the enum number to a
  // string on selection change.
  readonly DuplicateDetectionScope = DuplicateDetectionScope;

  // Target for the open reprocessing modal; null means closed.
  reextractTarget = signal<DocumentTypeDto | null>(null);
  reclassifyTarget = signal<DocumentTypeDto | null>(null);

  // Target + visibility for the ABP resource-permission dialog (#629). Two signals rather than
  // one nullable, because the dialog owns [(visible)] two-way and needs somewhere writable that
  // isn't the target itself.
  permissionsTarget = signal<DocumentTypeDto | null>(null);
  permissionsVisible = signal(false);

  allTypes = signal<DocumentTypeDto[]>([]);
  types = signal<ClientPagedResult<DocumentTypeDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  showDeleted = signal(false);
  importOpen = signal(false);

  // null = closed; 'create' / DocumentTypeDto = open in the matching mode.
  editing = signal<DocumentTypeDto | 'create' | null>(null);
  isSubmitting = signal(false);
  isSuggesting = signal(false);

  private slugHandle?: SlugSuggestionHandle;
  private tableQuery: Partial<ABP.PageQueryParams> = {};

  readonly form = this.fb.nonNullable.group({
    typeCode: [
      '',
      [
        Validators.required,
        Validators.maxLength(MAX_TYPE_CODE_LENGTH),
        Validators.pattern(TYPE_CODE_PATTERN),
      ],
    ],
    displayName: ['', [Validators.required, Validators.maxLength(MAX_DISPLAY_NAME_LENGTH)]],
    // Optional classification helper description (#262): only helps AI identify the type and does not
    // participate in document content processing.
    description: ['', [Validators.maxLength(MAX_DESCRIPTION_LENGTH)]],
    confidenceThreshold: [0.7, [Validators.required, Validators.min(0), Validators.max(1)]],
    priority: [0, [Validators.required]],
    // #651: what counts as a duplicate for this type. Default Layer matches DuplicateDetectionScope's
    // own default so a type left untouched behaves exactly as before the setting existed.
    duplicateScope: [DuplicateDetectionScope.Layer, [Validators.required]],
  });

  constructor() {
    configureEntityTable<DocumentTypeDto>(this.extensions, EXTRACT_TABLES.DocumentTypes, [
      EntityProp.create<DocumentTypeDto>({
        type: ePropType.String,
        name: 'typeCode',
        displayName: '::DocumentType:TypeCode',
        sortable: true,
        columnWidth: 240,
        valueResolver: data =>
          of(`<span class="badge bg-info text-dark">${escapeHtmlChars(data.record.typeCode)}</span>`),
      }),
      EntityProp.create<DocumentTypeDto>({
        type: ePropType.String,
        name: 'displayName',
        displayName: '::DocumentType:DisplayName',
        sortable: true,
      }),
      EntityProp.create<DocumentTypeDto>({
        type: ePropType.Number,
        name: 'confidenceThreshold',
        displayName: '::DocumentType:ConfidenceThreshold',
        sortable: true,
        columnWidth: 190,
        valueResolver: data => of(`${((data.record.confidenceThreshold ?? 0) * 100).toFixed(0)}%`),
      }),
      EntityProp.create<DocumentTypeDto>({
        type: ePropType.Number,
        name: 'priority',
        displayName: '::DocumentType:Priority',
        sortable: true,
        columnWidth: 140,
      }),
      EntityProp.create<DocumentTypeDto>({
        type: ePropType.String,
        name: 'duplicateScope',
        displayName: '::DocumentType:DuplicateScope',
        sortable: true,
        columnWidth: 170,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          const scope = data.record.duplicateScope ?? DuplicateDetectionScope.Layer;
          const key = scope === DuplicateDetectionScope.Uploader
            ? '::DocumentType:DuplicateScope:Uploader'
            : '::DocumentType:DuplicateScope:Layer';
          return of(escapeHtmlChars(localization.instant(key)));
        },
      }),
    ]);

  }

  ngOnInit(): void {
    this.hookTableQuery();
    this.slugHandle = wireSlugSuggestion({
      displayName: this.form.controls.displayName,
      target: this.form.controls.typeCode,
      suggest: text => this.slugService.suggest({ label: text }, undefined).pipe(map(r => r.slug ?? '')),
      fallback: () => this.nextTypeCode(),
      destroyRef: this.destroyRef,
      onPending: pending => this.isSuggesting.set(pending),
    });
    this.load();
  }

  // Local fallback when the LLM is unavailable or does not translate: choose the smallest type_{n} that
  // does not conflict with existing type codes.
  private nextTypeCode(): string {
    const existing = new Set(this.allTypes().map(t => t.typeCode));
    let i = 1;
    while (existing.has(`type_${i}`)) i++;
    return `type_${i}`;
  }

  refresh(): void {
    this.load();
  }

  toggleDeleted(): void {
    this.showDeleted.update(v => !v);
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    const source$ = this.showDeleted() ? this.service.getDeleted() : this.service.getVisible();
    source$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.allTypes.set(list);
        this.list.totalCount = list.length;
        this.applyTableQuery();
        this.isLoading.set(false);
      },
      error: () => {
        this.allTypes.set([]);
        this.types.set({ totalCount: 0, items: [] });
        this.list.totalCount = 0;
        this.isLoading.set(false);
      },
    });
  }

  private hookTableQuery(): void {
    this.list.query$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(query => this.applyTableQuery(query));
  }

  private applyTableQuery(query: Partial<ABP.PageQueryParams> = this.tableQuery): void {
    this.tableQuery = query;
    this.types.set(pageClientItems(this.allTypes(), query, DOCUMENT_TYPE_SORTS));
  }

  openCreate(): void {
    this.form.reset({
      typeCode: '',
      displayName: '',
      description: '',
      confidenceThreshold: 0.7,
      priority: 0,
      duplicateScope: DuplicateDetectionScope.Layer,
    });
    this.form.controls.typeCode.enable();
    // Must be called after form.reset()/enable(): both trigger valueChanges that can be misread as
    // "manual edit". reset() clears that marker and resets suggestion state, including the spinner.
    this.slugHandle?.reset();
    this.editing.set('create');
  }

  openEdit(type: DocumentTypeDto): void {
    // Disable before reset so slug auto-suggestion sees edit-mode reset as not automatically managed and
    // does not clear the existing typeCode as a stale key. See wireSlugSuggestion comments.
    this.form.controls.typeCode.disable();
    this.form.reset({
      typeCode: type.typeCode,
      displayName: type.displayName,
      description: type.description ?? '',
      confidenceThreshold: type.confidenceThreshold,
      priority: type.priority,
      duplicateScope: type.duplicateScope ?? DuplicateDetectionScope.Layer,
    });
    this.form.controls.typeCode.enable();
    this.slugHandle?.markManual();
    this.editing.set(type);
  }

  // Display-name blur triggers slug auto-suggestion. Measured feedback changed this from pause debounce
  // to blur trigger.
  onDisplayNameBlur(): void {
    this.slugHandle?.notifyDisplayNameBlur();
  }

  // Backdrop close guard: close only when both mousedown and click occur on the backdrop itself, not
  // inside the dialog.
  // Otherwise, dragging selected text inside an input and releasing over the backdrop can make the
  // browser fire click on the backdrop, the nearest common ancestor of mousedown/mouseup, closing the
  // modal and losing entered content. Recording the mousedown origin is the only reliable way to know
  // whether this click truly started from the backdrop.
  private backdropMouseDownOnSelf = false;

  onBackdropMouseDown(event: MouseEvent): void {
    this.backdropMouseDownOnSelf = event.target === event.currentTarget;
  }

  onBackdropClick(event: MouseEvent): void {
    if (this.backdropMouseDownOnSelf && event.target === event.currentTarget) {
      this.closeModal();
    }
    this.backdropMouseDownOnSelf = false;
  }

  closeModal(): void {
    this.editing.set(null);
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const mode = this.editing();
    if (mode === null) return;

    const raw = this.form.getRawValue();

    if (mode === 'create') {
      // #651: a newly created type has no documents yet, so switching its scope has nothing to
      // reconcile — never preview on create.
      this.isSubmitting.set(true);
      const input: CreateDocumentTypeDto = {
        typeCode: raw.typeCode,
        displayName: raw.displayName,
        description: raw.description.trim() || undefined,
        confidenceThreshold: raw.confidenceThreshold,
        priority: raw.priority,
        duplicateScope: raw.duplicateScope,
      };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::DocumentType:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
      return;
    }

    // #651 §5: only an actual switch has a consequence to preview; a type left at its current scope
    // saves straight through, with no preview call at all.
    const currentScope = mode.duplicateScope ?? DuplicateDetectionScope.Layer;
    if (raw.duplicateScope !== currentScope) {
      this.confirmDuplicateScopeChange(mode, raw);
      return;
    }

    this.saveUpdate(mode, raw);
  }

  /**
   * #651 §5: saving a scope switch enqueues automatic reconciliation of existing review flags — there is
   * no separate "apply" step — so the admin must decide "do I want this rule" before the save, not after.
   * The type form previews the two counts the switch would re-evaluate and confirms before the update
   * request goes out. Gated by the same permission as the update itself (server-side).
   */
  private confirmDuplicateScopeChange(mode: DocumentTypeDto, raw: DocumentTypeFormValue): void {
    this.isSubmitting.set(true);
    this.service.getDuplicateScopePreview(mode.id!, raw.duplicateScope)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: preview => {
          this.isSubmitting.set(false);
          this.confirmDuplicateScopeSwitch(mode, raw, preview);
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:Reprocess:PreviewFailed', '::Error');
        },
      });
  }

  private confirmDuplicateScopeSwitch(
    mode: DocumentTypeDto,
    raw: DocumentTypeFormValue,
    preview: DuplicateScopePreviewDto,
  ): void {
    // Both directions re-evaluate every fingerprinted document of the type against the prospective
    // scope, and either direction can both flag and clear — a narrowing switch can also newly flag a
    // stale first-upload gap, not only release documents. One message, two counts; no direction branch.
    this.confirmation
      .warn('::DocumentType:DuplicateScope:Preview:Message', '::DocumentType:DuplicateScope:Preview:Title', {
        messageLocalizationParams: [String(preview.willFlagCount ?? 0), String(preview.willClearCount ?? 0)],
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.saveUpdate(mode, raw);
      });
  }

  private saveUpdate(mode: DocumentTypeDto, raw: DocumentTypeFormValue): void {
    this.isSubmitting.set(true);
    this.service.update(mode.id!, {
      typeCode: raw.typeCode,
      displayName: raw.displayName,
      description: raw.description.trim() || undefined,
      confidenceThreshold: raw.confidenceThreshold,
      priority: raw.priority,
      duplicateScope: raw.duplicateScope,
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.onSaved('::DocumentType:UpdatedSuccessfully'),
        error: () => this.isSubmitting.set(false),
      });
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, '::Success');
    this.onDocumentTypesChanged();
  }

  /**
   * A write landed on the document-type layer (#635). Two things follow: this page re-reads its own list,
   * and the shared {@link DocumentTypesStore} — which the document list, the detail page, the overview and
   * the upload card read for the rest of the session — is invalidated.
   *
   * The invalidation lives at the write sites rather than on each reader's init on purpose. Before #635
   * every reading page re-fetched the visible types on navigation, which hid this problem behind four
   * redundant requests; the store's whole point is that the answer does not change on its own. It changes
   * here, and nowhere else in this SPA, so this is where it is announced. Only a SUCCESSFUL write calls
   * this: a failed create leaves the layer exactly as the store already has it.
   */
  private onDocumentTypesChanged(): void {
    this.load();
    this.documentTypes.reload();
  }

  delete(type: DocumentTypeDto): void {
    this.confirmation
      .warn('::DocumentType:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(type.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::DocumentType:DeletedSuccessfully', '::Success');
              this.onDocumentTypesChanged();
            },
            error: () => this.toaster.error('::DocumentType:DeleteFailed', '::Error'),
          });
      });
  }

  restore(type: DocumentTypeDto): void {
    this.service.restore(type.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.toaster.success('::DocumentType:RestoredSuccessfully', '::Success');
          this.onDocumentTypesChanged();
        },
      });
  }

  /**
   * #635: a config-pack import creates and updates document types, so it invalidates the shared store for
   * the same reason the four CRUD writes do — it is simply a bulk one. It has its own handler rather than
   * reusing {@link refresh}, because Refresh is a READ and must not reload the store.
   */
  onPackImported(): void {
    this.onDocumentTypesChanged();
  }

  manageFields(type: DocumentTypeDto): void {
    this.router.navigate(['/documents/types', type.id, 'fields']);
  }

  openReextractFields(type: DocumentTypeDto): void {
    this.reextractTarget.set(type);
  }

  openReclassify(type: DocumentTypeDto): void {
    this.reclassifyTarget.set(type);
  }

  // #629: opens ABP's own resource-permission dialog for this type's Upload grant. The dialog,
  // its endpoints (/api/permission-management/permissions/resource…) and its user/role search
  // are all ABP's; gated server-side by DocumentTypes.ManagePermissions.
  /**
   * The dialog's own visibility, relayed rather than two-way bound, because a close has two consequences
   * beyond the flag.
   *
   * Re-initialising the target means re-opening the dialog for another row does not carry over the
   * previous one. And a close is the only signal this page gets that the grants may have changed: ABP's
   * `ResourcePermissionManagementComponent` exposes `visible` and nothing else — no "saved" output,
   * `savePermission()` is internal — so a save cannot be told from a cancel. Reloading on every close is
   * the conservative reading (#635): one wasted `GetVisibleAsync` when the operator cancels, against an
   * `Upload` grant that would otherwise silently fail to reach the upload picker and the type pickers for
   * the rest of the session. Only a transition out of "open" counts, so nothing fires on page load.
   */
  onPermissionsVisibleChange(visible: boolean): void {
    const wasOpen = this.permissionsVisible();
    this.permissionsVisible.set(visible);
    if (visible || !wasOpen) {
      return;
    }
    this.permissionsTarget.set(null);
    this.documentTypes.reload();
  }

  openPermissions(type: DocumentTypeDto): void {
    this.permissionsTarget.set(type);
    this.permissionsVisible.set(true);
  }

  openImport(): void {
    this.importOpen.set(true);
  }

  // Export a single type's config (the type + its field definitions) as a downloadable pack (#444).
  exportOne(type: DocumentTypeDto): void {
    this.packService
      .export(type.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: pack => this.downloadPacks(pack, type.typeCode!),
        error: () => this.toaster.error('::DocumentType:Pack:ExportFailed', '::Error'),
      });
  }

  // Export the whole current layer's config as one pack file — the private-deployment migration path.
  exportAll(): void {
    this.packService
      .exportAll()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: packs => {
          if (packs.length === 0) {
            this.toaster.info('::DocumentType:Pack:NothingToExport');
            return;
          }
          this.downloadPacks(packs, 'document-types');
        },
        error: () => this.toaster.error('::DocumentType:Pack:ExportFailed', '::Error'),
      });
  }

  private downloadPacks(packs: DocumentTypePackDto | DocumentTypePackDto[], label: string): void {
    const blob = new Blob([serializePacks(packs)], { type: 'application/json' });
    triggerBlobDownload(blob, packFileName(label, new Date()));
  }
}

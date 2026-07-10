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
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
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
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { of } from 'rxjs';
import {
  CabinetDto,
  CabinetService,
  CreateExportTemplateDto,
  DocumentLifecycleStatus,
  DocumentTypeDto,
  DocumentTypeService,
  ExportColumnInput,
  ExportFormat,
  ExportTemplateDto,
  ExportTemplateService,
  FieldDefinitionDto,
  FieldDefinitionService,
  EXTRACT_PERMISSIONS,
  documentLifecycleStatusOptions,
  exportFormatOptions,
} from '@dignite/vault-extract';
import {
  ClientPagedResult,
  configureEntityTable,
  pageClientItems,
  EXTRACT_TABLES,
  SortAccessors,
} from '../../shared/extensible-table';
import { exportFileName, readBlobErrorMessage, triggerBlobDownload } from '../../shared/blob-download';

// Mirrors ExportTemplateConsts (Domain.Shared).
const MAX_NAME_LENGTH = 128;
const MAX_COLUMN_COUNT = 100;

const EXPORT_TEMPLATE_SORTS: SortAccessors<ExportTemplateDto> = {
  name: template => template.name,
  format: template => template.format,
  documentTypeId: template => template.documentTypeId,
  columns: template => template.columns?.length ?? 0,
};

@Component({
  selector: 'lib-export-template-list',
  templateUrl: './export-template-list.component.html',
  styleUrls: ['./export-template-list.component.scss'],
  imports: [
    CommonModule,
    ReactiveFormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: EXTRACT_TABLES.ExportTemplates,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExportTemplateListComponent implements OnInit {
  private readonly service = inject(ExportTemplateService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly cabinetService = inject(CabinetService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);

  readonly list = inject(ListService);

  readonly canManage = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Templates.Default,
  );
  readonly canExport = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Export,
  );

  readonly formatOptions = exportFormatOptions;
  readonly ExportFormat = ExportFormat;
  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly lifecycleStatusOptions = documentLifecycleStatusOptions;

  allTemplates = signal<ExportTemplateDto[]>([]);
  templates = signal<ClientPagedResult<ExportTemplateDto>>({ totalCount: 0, items: [] });
  documentTypes = signal<DocumentTypeDto[]>([]);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  cabinets = signal<CabinetDto[]>([]);
  isLoading = signal(true);
  editing = signal<ExportTemplateDto | 'create' | null>(null);
  isSubmitting = signal(false);
  exportingId = signal<string | null>(null);
  /** Export template currently configuring filters; when non-null, show the filter modal. */
  filteringTemplate = signal<ExportTemplateDto | null>(null);
  private tableQuery: Partial<ABP.PageQueryParams> = {};

  readonly filterForm = this.fb.nonNullable.group({
    lifecycleStatus: [null as DocumentLifecycleStatus | null],
    cabinetId: [null as string | null],
    creationTimeMin: [null as string | null],
    creationTimeMax: [null as string | null],
  });

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(MAX_NAME_LENGTH)]],
    format: [ExportFormat.Csv, [Validators.required]],
    documentTypeId: ['', [Validators.required]],
    columns: this.fb.array<FormGroup>([]),
  });

  get columns(): FormArray<FormGroup> {
    return this.form.controls.columns;
  }

  constructor() {
    configureEntityTable<ExportTemplateDto>(this.extensions, EXTRACT_TABLES.ExportTemplates, [
      EntityProp.create<ExportTemplateDto>({
        type: ePropType.String,
        name: 'name',
        displayName: '::ExportTemplate:Name',
        sortable: true,
      }),
      EntityProp.create<ExportTemplateDto>({
        type: ePropType.String,
        name: 'format',
        displayName: '::ExportTemplate:Format',
        sortable: true,
        columnWidth: 150,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          return of(`<span class="badge bg-secondary">${escapeHtmlChars(localization.instant('::ExportFormat:' + this.formatLabel(data.record.format)))}</span>`);
        },
      }),
      EntityProp.create<ExportTemplateDto>({
        type: ePropType.String,
        name: 'documentTypeId',
        displayName: '::ExportTemplate:DocumentType',
        sortable: true,
        columnWidth: 220,
        valueResolver: data => {
          const label = this.documentTypeLabel(data.record.documentTypeId);
          return of(label
            ? `<span class="badge bg-info text-dark">${escapeHtmlChars(label)}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<ExportTemplateDto>({
        type: ePropType.Number,
        name: 'columns',
        displayName: '::ExportTemplate:Columns',
        sortable: true,
        columnWidth: 150,
        valueResolver: data => of(data.record.columns?.length ?? 0),
      }),
    ]);
  }

  ngOnInit(): void {
    this.hookTableQuery();
    this.load();
    this.loadDocumentTypes();
    this.loadCabinets();
  }

  refresh(): void {
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.service
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.allTemplates.set(list);
          this.list.totalCount = list.length;
          this.applyTableQuery();
          this.isLoading.set(false);
        },
        error: () => {
          this.allTemplates.set([]);
          this.templates.set({ totalCount: 0, items: [] });
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
    this.templates.set(pageClientItems(this.allTemplates(), query, EXPORT_TEMPLATE_SORTS));
  }

  private loadDocumentTypes(): void {
    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.documentTypes.set(list);
          this.applyTableQuery();
        },
      });
  }

  private loadCabinets(): void {
    this.cabinetService
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: list => this.cabinets.set(list) });
  }

  openCreate(): void {
    this.columns.clear();
    this.fieldDefinitions.set([]);
    this.form.reset({ name: '', format: ExportFormat.Csv, documentTypeId: '' });
    this.addColumn();
    this.editing.set('create');
  }

  openEdit(template: ExportTemplateDto): void {
    this.columns.clear();
    this.form.reset({
      name: template.name,
      format: template.format,
      documentTypeId: template.documentTypeId,
    });
    this.loadFieldDefinitions(template.documentTypeId);
    [...(template.columns ?? [])]
      .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
      .forEach(c => this.addColumn(c.fieldDefinitionId));
    this.editing.set(template);
  }

  addColumn(fieldDefinitionId = ''): void {
    this.columns.push(
      this.fb.nonNullable.group({
        fieldDefinitionId: [fieldDefinitionId, [Validators.required]],
      }),
    );
  }

  removeColumn(index: number): void {
    this.columns.removeAt(index);
  }

  // Switching the document type invalidates already-picked field columns (they belong to the prior type).
  onDocumentTypeChange(): void {
    const documentTypeId = this.form.controls.documentTypeId.value;
    if (this.editing() === 'create') {
      // #414: a new download config defaults to ALL of the type's fields (operator removes rather than
      // adds), so replace the columns with the full field set once the definitions load.
      this.loadFieldDefinitions(documentTypeId, true);
    } else {
      // Edit mode: the type's fields changed, so previously-picked columns are stale — clear their field
      // selection but keep the rows; do not auto-replace an existing saved config.
      this.columns.controls.forEach(ctrl => ctrl.get('fieldDefinitionId')?.setValue(''));
      this.loadFieldDefinitions(documentTypeId, false);
    }
  }

  private loadFieldDefinitions(documentTypeId: string | undefined, prefillAllColumns = false): void {
    if (!documentTypeId) {
      this.fieldDefinitions.set([]);
      if (prefillAllColumns) {
        this.columns.clear();
        this.addColumn();
      }
      return;
    }
    this.fieldDefinitionService
      .getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: defs => {
          this.fieldDefinitions.set(defs);
          if (prefillAllColumns) {
            this.prefillAllColumns(defs);
          }
        },
        error: () => this.fieldDefinitions.set([]),
      });
  }

  // #414: seed a new download config with every field of the chosen type (ordered by displayOrder), so
  // "all fields" is the default and the operator removes the unwanted ones. Respects the column cap and
  // surfaces a notice rather than silently truncating; an empty type keeps one blank row.
  private prefillAllColumns(defs: FieldDefinitionDto[]): void {
    const ordered = [...defs].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0));
    this.columns.clear();
    if (ordered.length === 0) {
      this.addColumn();
      return;
    }
    ordered.slice(0, MAX_COLUMN_COUNT).forEach(d => this.addColumn(d.id));
    if (ordered.length > MAX_COLUMN_COUNT) {
      this.toaster.info('::ExportTemplate:ColumnCapNotice', '::Info');
    }
  }

  closeModal(): void {
    this.editing.set(null);
  }

  submit(): void {
    if (this.form.invalid || this.columns.length === 0) {
      this.form.markAllAsTouched();
      return;
    }
    const mode = this.editing();
    if (mode === null) return;

    this.isSubmitting.set(true);
    const raw = this.form.getRawValue();
    // Order = array position; the editor's row order is the source of truth.
    const columns: ExportColumnInput[] = this.columns.controls.map((ctrl, i) => {
      const v = ctrl.getRawValue() as { fieldDefinitionId: string };
      return { fieldDefinitionId: v.fieldDefinitionId, order: i };
    });

    if (mode === 'create') {
      const input: CreateExportTemplateDto = {
        name: raw.name,
        format: raw.format,
        documentTypeId: raw.documentTypeId,
        columns,
      };
      this.service
        .create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::ExportTemplate:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service
        .update(mode.id!, {
          name: raw.name,
          format: raw.format,
          documentTypeId: raw.documentTypeId,
          columns,
        })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::ExportTemplate:UpdatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    }
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, '::Success');
    this.load();
  }

  delete(template: ExportTemplateDto): void {
    this.confirmation
      .warn('::ExportTemplate:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service
          .delete(template.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::ExportTemplate:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::ExportTemplate:DeleteFailed', '::Error'),
          });
      });
  }

  exportTemplate(template: ExportTemplateDto): void {
    this.filterForm.reset({ lifecycleStatus: null, cabinetId: null, creationTimeMin: null, creationTimeMax: null });
    this.filteringTemplate.set(template);
  }

  closeFilterModal(): void {
    this.filteringTemplate.set(null);
  }

  startExport(): void {
    const template = this.filteringTemplate();
    if (!template) return;

    const f = this.filterForm.getRawValue();
    this.exportingId.set(template.id!);
    this.filteringTemplate.set(null);

    this.service
      .export({
        templateId: template.id!,
        lifecycleStatus: f.lifecycleStatus ?? undefined,
        cabinetId: f.cabinetId ?? undefined,
        creationTimeMin: f.creationTimeMin ?? undefined,
        creationTimeMax: f.creationTimeMax ?? undefined,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          triggerBlobDownload(blob, exportFileName(template.name!, template.format === ExportFormat.Xlsx));
          this.exportingId.set(null);
        },
        // #496: this used to swallow the failure. The over-limit fail-fast
        // (Extract:ExportDocumentLimitExceeded) exists to tell the operator to narrow the filter rather
        // than hand them a truncated ledger — silence defeated it. The message rides inside a Blob body
        // because the request is responseType:'blob', so ABP's global interceptor never sees it.
        error: (err: unknown) => {
          this.exportingId.set(null);
          void readBlobErrorMessage(err).then(message =>
            this.toaster.error(
              message ? escapeHtmlChars(message) : '::ExportTemplate:ExportFailed',
              '::Error',
            ),
          );
        },
      });
  }

  formatLabel(format: ExportFormat | undefined): string {
    return this.formatOptions.find(o => o.value === format)?.key ?? String(format);
  }

  documentTypeLabel(documentTypeId: string | undefined): string | null {
    return this.documentTypes().find(dt => dt.id === documentTypeId)?.typeCode ?? null;
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  OnDestroy,
  inject,
  signal,
  computed,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { of, switchMap } from 'rxjs';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { LocalizationPipe, LocalizationService, PermissionService } from '@abp/ng.core';
import { DynamicFormComponent, type FormFieldConfig } from '@abp/ng.components/dynamic-form';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentDto,
  DocumentLifecycleStatus,
  DocumentPipelineRunDto,
  DocumentReviewStatus,
  DocumentService,
  DocumentTypeService,
  FieldDataType,
  FieldDefinitionDto,
  FieldDefinitionService,
  PAPERBASE_PERMISSIONS,
  PipelineRunStatus,
} from '@dignite/paperbase';

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

// Mirrors core/src/Dignite.Paperbase.Domain.Shared/Documents/PaperbasePipelines.cs.
const KNOWN_PIPELINE_CODES = [
  'text-extraction',
  'classification',
] as const;

@Component({
  selector: 'lib-document-detail',
  templateUrl: './document-detail.component.html',
  styleUrls: ['./document-detail.component.scss'],
  imports: [CommonModule, RouterModule, DynamicFormComponent, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentDetailComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly toaster = inject(ToasterService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly permissionService = inject(PermissionService);
  private readonly localization = inject(LocalizationService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );
  readonly canEditFields = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  isTextExpanded = signal(false);
  imageError = signal(false);
  retryingPipeline = signal<string | null>(null);
  blobUrl = signal<string | null>(null);
  isBlobLoading = signal(false);
  isEditingFields = signal(false);
  isSavingFields = signal(false);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  extractedFieldFormFields = signal<FormFieldConfig[]>([]);

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;
  readonly PipelineRunStatus = PipelineRunStatus;

  pipelineRows = computed<PipelineRow[]>(() => {
    const doc = this.document();
    if (!doc) return [];

    const allRuns = doc.pipelineRuns ?? [];
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
          .filter(code => !!code && !KNOWN_PIPELINE_CODES.includes(code as typeof KNOWN_PIPELINE_CODES[number]))
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

  needsReview = computed(() =>
    this.document()?.reviewStatus === DocumentReviewStatus.PendingReview
  );

  isProcessing = computed(() => {
    if (this.needsReview()) return false;

    const status = this.document()?.lifecycleStatus;
    return status === DocumentLifecycleStatus.Uploaded ||
           status === DocumentLifecycleStatus.Processing;
  });

  isReady = computed(() =>
    this.document()?.lifecycleStatus === DocumentLifecycleStatus.Ready
  );

  isImage = computed(() =>
    this.document()?.fileOrigin?.contentType?.startsWith('image/') ?? false
  );

  // Type-bound extracted fields (field architecture v2). Key = field name; value
  // is assembled server-side from the DocumentExtractedField rows (#206). Sorted by
  // key for a stable display order.
  extractedFieldEntries = computed<{ key: string; value: string }[]>(() => {
    const fields = this.document()?.extractedFields;
    if (!fields) return [];
    return Object.keys(fields)
      .sort((a, b) => a.localeCompare(b))
      .map(key => ({ key, value: this.formatFieldValue(fields[key]) }));
  });

  // 字段卡片显示条件：已有抽取值（只读展示），或可编辑且该类型有字段定义（支持补全空字段）。
  showFieldsCard = computed(() =>
    this.extractedFieldEntries().length > 0 ||
    (this.canEditFields && this.fieldDefinitions().length > 0)
  );

  private documentId!: string;

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    this.loadDocument();
  }

  refresh(): void {
    this.loadDocument();
  }

  private loadDocument(): void {
    this.isLoading.set(true);
    this.documentService.get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: doc => {
        this.document.set(doc);
        this.isLoading.set(false);
        // 仅图片需要立即加载（内联预览）；非图片等用户点击"打开文件"时再下载
        if (doc.fileOrigin?.contentType?.startsWith('image/')) {
          this.loadBlob();
        }
        // 编辑字段需要该类型的字段定义（含 LLM 漏抽的空字段）以支持补全。
        // getList 需 ConfirmClassification 权限，仅在可编辑时拉取避免 403。
        this.fieldDefinitions.set([]);
        if (this.canEditFields && doc.documentTypeCode) {
          this.loadFieldDefinitions(doc.documentTypeCode);
        }
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  // doc.documentTypeCode 是 Document 出口契约的当前 code 投影（#207）；字段定义 API 按不可变
  // DocumentTypeId 关联，故先在当前层可见类型里把 code 解析为 id 再查。
  private loadFieldDefinitions(typeCode: string): void {
    this.documentTypeService.getVisible()
      .pipe(
        switchMap(types => {
          const documentTypeId = types.find(t => t.typeCode === typeCode)?.id;
          if (!documentTypeId) return of<FieldDefinitionDto[]>([]);
          return this.fieldDefinitionService.getList({ documentTypeId });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: defs => this.fieldDefinitions.set(
          [...defs].sort((a, b) => a.displayOrder - b.displayOrder || a.name.localeCompare(b.name)),
        ),
        error: () => this.fieldDefinitions.set([]),
      });
  }

  private loadBlob(): void {
    const oldUrl = this.blobUrl();
    if (oldUrl) URL.revokeObjectURL(oldUrl);
    this.blobUrl.set(null);

    this.documentService.getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => this.blobUrl.set(URL.createObjectURL(blob)),
      });
  }

  openFile(): void {
    const existing = this.blobUrl();
    if (existing) {
      window.open(existing, '_blank');
      return;
    }
    this.isBlobLoading.set(true);
    this.documentService.getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const url = URL.createObjectURL(blob);
          this.blobUrl.set(url);
          this.isBlobLoading.set(false);
          window.open(url, '_blank');
        },
        error: () => {
          this.isBlobLoading.set(false);
        },
      });
  }

  ngOnDestroy(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
  }

  onImageError(): void {
    this.imageError.set(true);
  }

  toggleText(): void {
    this.isTextExpanded.set(!this.isTextExpanded());
  }

  goBack(): void {
    this.router.navigate(['/documents']);
  }

  delete(): void {
    const doc = this.document();
    if (!doc) return;
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.delete(doc.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
          next: () => {
            this.toaster.success('::Document:DeletedSuccessfully', '::Success');
            this.router.navigate(['/documents']);
          },
        });
      });
  }

  getStatusBadgeClass(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return 'badge bg-secondary';
      case DocumentLifecycleStatus.Processing: return 'badge bg-warning text-dark';
      case DocumentLifecycleStatus.Ready:      return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:     return 'badge bg-danger';
      default:                                 return 'badge bg-secondary';
    }
  }

  getDocumentStatusBadgeClass(doc: DocumentDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return 'badge bg-warning text-dark';
    }

    return this.getStatusBadgeClass(doc.lifecycleStatus);
  }

  getStatusLabel(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing: return '::Document:Status:Processing';
      case DocumentLifecycleStatus.Ready:      return '::Document:Status:Ready';
      case DocumentLifecycleStatus.Failed:     return '::Document:Status:Failed';
      default:                                 return '::Document:Status:Unknown';
    }
  }

  getDocumentStatusLabel(doc: DocumentDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return '::DocumentReviewStatus:PendingReview';
    }

    return this.getStatusLabel(doc.lifecycleStatus);
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
    this.documentService.retryPipeline(this.documentId, pipelineCode)
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
    if (value === null || value === undefined) return '—';
    // 多值字段（#212）出口是 JSON 数组 → 以 ", " 连接展示（空数组当无值）。
    if (Array.isArray(value)) {
      return value.length > 0 ? value.map(v => String(v)).join(', ') : '—';
    }
    if (typeof value === 'object') return JSON.stringify(value);
    return String(value);
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
      const key = def.name;
      const value = formValue[key];

      if (this.shouldOmitFieldValue(value)) continue;
      fields[key] = this.coerceValue(def, value);
    }

    this.documentService.updateExtractedFields(doc.id, fields)
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
        key: def.name,
        label: `${def.displayName} (${def.name})`,
        // 多值字段（#212，仅 String）用 textarea 每行一个值；单值按 DataType 选输入类型。
        type: def.allowMultiple ? 'textarea' : this.toFormFieldType(def.dataType),
        value: this.toFormInitialValue(def, values[def.name]),
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

  private toFormFieldType(dataType: FieldDataType): FormFieldConfig['type'] {
    switch (dataType) {
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
    // 多值字段（#212）：出口数组 → textarea 每行一个值。非数组（含 null/未抽取）→ 空。
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

  // 按字段 DataType 转成对应 JSON 类型。Date/DateTime/String 一律存字符串。
  private coerceValue(def: FieldDefinitionDto, value: unknown): unknown {
    // 多值字段（#212）：textarea 每行一个值 → 去空白 + 去空行 → string[]（与后端 UpdateExtractedFieldsAsync 收数组对称）。
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
    return matches.reduce((prev, curr) => (curr.attemptNumber > prev.attemptNumber ? curr : prev));
  }
}

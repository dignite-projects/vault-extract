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
import { forkJoin, of, switchMap, tap } from 'rxjs';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
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
  DocumentReviewStatus,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
  FieldDataType,
  FieldDefinitionDto,
  FieldDefinitionService,
  PAPERBASE_PERMISSIONS,
  PipelineRunStatus,
} from '@dignite/paperbase';
import { formatExtractedFieldValue } from '../../shared/format-field-value';

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
  imports: [CommonModule, RouterModule, FormsModule, DynamicFormComponent, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentDetailComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
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

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );
  readonly canEditFields = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Cabinets.Default,
  );

  document = signal<DocumentDto | null>(null);
  // #216：PipelineRun 已拆为独立聚合根，从 DocumentDto.pipelineRuns 移除——
  // 改为独立 signal，loadDocument 时通过 DocumentPipelineRunService 单独拉取。
  pipelineRuns = signal<DocumentPipelineRunDto[]>([]);
  isLoading = signal(true);
  isTextExpanded = signal(false);
  imageError = signal(false);
  retryingPipeline = signal<string | null>(null);
  isRerecognizing = signal(false);
  blobUrl = signal<string | null>(null);
  isEditingFields = signal(false);
  isSavingFields = signal(false);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  extractedFieldFormFields = signal<FormFieldConfig[]>([]);
  // 文档所属文件柜候选：cabinetId → name 映射展示 + 改派下拉候选（#257）；需 Cabinets.Default 才加载。
  cabinets = signal<CabinetDto[]>([]);
  // 文件柜改派（#257）编辑态：进入编辑显示下拉；selectedCabinetId 空串 = 未归类。
  isEditingCabinet = signal(false);
  isSavingCabinet = signal(false);
  selectedCabinetId = signal<string>('');
  // 当前层可见文档类型（typeCode → displayName 映射）；随字段定义加载时一并填充。
  documentTypes = signal<DocumentTypeDto[]>([]);

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;
  readonly PipelineRunStatus = PipelineRunStatus;

  pipelineRows = computed<PipelineRow[]>(() => {
    if (!this.document()) return [];

    // #216：runs 来源从 doc.pipelineRuns 改为独立 pipelineRuns signal（DocumentPipelineRunService）。
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

  // 关键流水线（文本提取 / 分类）有进行中的 run（Pending/Running）时为 true。
  pipelineInProgress = computed(() =>
    this.pipelineRows().some(r => r.isKnown && r.inProgress)
  );

  // #263「重新识别」可用性：已提取文本 + 有 ConfirmClassification 权限（同 canEditFields）+
  // 当前无进行中的关键流水线、且不在加载中——避免对正在(重)处理的文档叠加重排。
  // 用 pipelineInProgress 而非 !isProcessing()：后者在 needsReview() 时恒 false，会让待审核文档
  // 在重排进行中仍露出按钮（评审 #5）。POST 在途由按钮 [disabled]="isRerecognizing()" 兜住。
  canRerecognize = computed(() =>
    this.canEditFields &&
    !!this.document()?.markdown &&
    !this.pipelineInProgress() &&
    !this.isLoading()
  );

  isImage = computed(() =>
    this.document()?.fileOrigin?.contentType?.startsWith('image/') ?? false
  );

  // 文档所属文件柜名（cabinetId → name）；未归类或解析不到（无权限 / 已删柜）返回 null。
  cabinetName = computed<string | null>(() => {
    const id = this.document()?.cabinetId;
    if (!id) return null;
    return this.cabinets().find(c => c.id === id)?.name ?? null;
  });

  // 文档类型 displayName（typeCode → displayName）；未分类返回 null，跨层 / 已删类型回退 code。
  documentTypeDisplayName = computed<string | null>(() => {
    const code = this.document()?.documentTypeCode;
    if (!code) return null;
    return this.documentTypes().find(t => t.typeCode === code)?.displayName ?? code;
  });

  // Type-bound extracted fields (field architecture v2)。只展示「当前活跃字段定义」对应的值：
  // 后端出口 ExtractedFields 穿透 soft-delete 仍含已删字段定义的历史值（给下游"数据不丢"，#206/#207），
  // 但操作员 UI 不再显示它们——与列表动态列（亦只用活跃定义）一致。label 用 displayName，按 displayOrder 排序。
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
    // doc + runs 互相独立（#216 后），并行拉一次；fieldDefinitions 依赖 doc.documentTypeCode 仍串行。
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
          // 仅图片需要立即加载（内联预览）；非图片等用户点击"打开文件"时再下载
          if (doc.fileOrigin?.contentType?.startsWith('image/')) {
            this.loadBlob();
          }
          // 字段定义用于：① 抽取字段展示用 displayName（所有查看者）；② 可编辑时补全空字段。
          // 后端 GetListAsync 自 #223 起对 Documents.Default 即放开，故只要有类型就加载，不再门控编辑权限。
          this.fieldDefinitions.set([]);
          if (doc.documentTypeCode) {
            this.loadFieldDefinitions(doc.documentTypeCode);
          }
          // 文件柜名映射：仅当有 Cabinets.Default 权限且尚未加载时拉取（无权限则文件柜行不显示）。
          if (this.canViewCabinets && this.cabinets().length === 0) {
            this.loadCabinets();
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
        // 一次 getVisible 两用：存 documentTypes 供文档类型 displayName 映射；再解析 typeId 查字段定义。
        tap(types => this.documentTypes.set(types)),
        switchMap(types => {
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

  // 文件柜候选（名映射展示 + 改派下拉，#257）；仅 Cabinets.Default 权限时调用。
  private loadCabinets(): void {
    this.cabinetService.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => this.cabinets.set(list),
        error: () => this.cabinets.set([]),
      });
  }

  // 文件柜改派（#257）——进入编辑态，预选当前柜（空串 = 未归类）。
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
    // 空串 → null（移出文件柜 / 未归类）。后端校验柜存在性 + 当前层归属。
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

  // 打开干净路由的文件预览页（documents/:id/file）——替代旧的 blob: 新标签直开。
  // 文件本体由预览页自己经带 token 的 getBlob 拉取内嵌，token 不进地址栏。
  openFile(): void {
    this.router.navigate(['/documents', this.documentId, 'file']);
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
        this.documentService.delete(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
          next: () => {
            this.toaster.success('::Document:DeletedSuccessfully', '::Success');
            this.router.navigate(['/documents']);
          },
        });
      });
  }

  // #263「重新识别」：让 AI 在现有 Markdown 上重跑自动分类 → 级联重抽字段（不重新 OCR）。
  // 覆盖性操作（覆盖当前类型 + 人工改过的字段值），先确认；成功后 reload 反映 Processing 态。
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

  getStatusBadgeClass(status: DocumentLifecycleStatus | undefined): string {
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
        // 多值字段（#212，仅文本）用 textarea 每行一个值；单值按 DataType 选输入类型。
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
      // 长文本（摘要 / 描述等）用多行编辑框；toFormInitialValue 的 default 分支已把它当字符串原样回填。
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

  // 按字段 DataType 转成对应 JSON 类型。Date/DateTime/Text 一律存字符串。
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
    return matches.reduce((prev, curr) => ((curr.attemptNumber ?? 0) > (prev.attemptNumber ?? 0) ? curr : prev));
  }
}

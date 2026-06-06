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
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
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
  imports: [CommonModule, FormsModule, DynamicFormComponent, LocalizationPipe],
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
  private readonly sanitizer = inject(DomSanitizer);
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
  // 左栏 3-Tab（#274）：默认 Markdown 预览；切到 'file' 才触发原文件 blob 懒加载。
  activeTab = signal<'preview' | 'source' | 'file'>('preview');
  isBlobLoading = signal(false);
  // 原文件预览失败（blob 下载失败 或 <img> 渲染失败）的统一信号；进 file Tab / reload 时复位。
  previewError = signal(false);
  retryingPipeline = signal<string | null>(null);
  isRerecognizing = signal(false);
  blobUrl = signal<string | null>(null);
  // PDF iframe 的 resource URL 必须经 DomSanitizer 放行（自造同源 blob:，安全）。
  safeBlobUrl = signal<SafeResourceUrl | null>(null);
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

  isPdf = computed(() =>
    this.document()?.fileOrigin?.contentType === 'application/pdf'
  );

  // Markdown 源文本中间 computed（#274 review）：document() 变更但 markdown 未变时（改字段 /
  // 文件柜等），返回相同字符串 → 下游 renderedMarkdown 凭值相等性短路，避免重复 marked.parse。
  private markdownSource = computed(() => this.document()?.markdown ?? '');

  // Markdown 预览（#274）：marked 渲染为 HTML 字符串，模板 [innerHTML] 绑定时由 Angular 内置
  // DomSanitizer 自动消毒（剥离 <script> / on* / javascript:）。绝不 bypassSecurityTrustHtml——
  // Markdown 是攻击者可影响内容（VLM OCR 可被图内文字 prompt-inject），消毒器必须全程开。
  renderedMarkdown = computed<string>(() => {
    const md = this.markdownSource();
    return md ? (marked.parse(md, { gfm: true, async: false }) as string) : '';
  });

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
  // 「下载文件」点击时 blob 尚未加载 → 置位，待 loadBlob 成功后触发一次下载（见 downloadFile）。
  private pendingDownload = false;

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
          // 原文件 blob 懒加载（#274）：默认不在加载文档时拉取，仅「原文件」Tab 才下载（见 selectTab）。
          // 若用户已在该 Tab，reload（Refresh / rerecognize）后调一次 ensureFilePreview——blob 已缓存
          // 则早退、上次失败则复位错误重试，避免「Refresh 对卡住的预览无效」（#274 review）。
          if (this.activeTab() === 'file') {
            this.ensureFilePreview();
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

  // 原文件 blob 懒加载（#274）：「原文件」Tab 激活或点击「下载文件」时调用。已加载 / 加载中则跳过，
  // blob 缓存至组件销毁（ngOnDestroy revoke）；预览与下载共用同一份缓存，不重复拉取。
  private loadBlob(): void {
    if (this.blobUrl() || this.isBlobLoading()) return;
    this.isBlobLoading.set(true);

    this.documentService.getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const url = URL.createObjectURL(blob);
          this.blobUrl.set(url);
          // PDF 走 iframe，resource URL 必须经 DomSanitizer 放行（自造同源 blob:，安全）。
          if (this.isPdf()) {
            this.safeBlobUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
          }
          this.isBlobLoading.set(false);
          this.flushPendingDownload(url);
        },
        error: () => {
          this.isBlobLoading.set(false);
          this.previewError.set(true);
          if (this.pendingDownload) {
            this.pendingDownload = false;
            this.toaster.error('::Document:DownloadFailed', '::Error');
          }
        },
      });
  }

  // 进入「原文件」Tab 的统一入口（#274 review）：先复位上次的预览错误（给 <img> 重建一次重试、
  // 让上次下载失败可重拉），再 loadBlob（自身防重复请求）。修复 imageError 卡死 + Refresh 无效。
  private ensureFilePreview(): void {
    this.previewError.set(false);
    this.loadBlob();
  }

  // Tab 切换（#274）：切到「原文件」时确保原文件预览就绪。
  selectTab(tab: 'preview' | 'source' | 'file'): void {
    this.activeTab.set(tab);
    if (tab === 'file') {
      this.ensureFilePreview();
    }
  }

  // 「下载文件」（footer）：直接触发浏览器下载原文件，免去先开预览页再下载。
  // blob 已缓存（看过「原文件」Tab 或此前下载过）则秒触发；未缓存则置 pendingDownload 复用
  // loadBlob 拉取（共享 isBlobLoading 防重复请求），blob 到位后由 flushPendingDownload 触发。
  downloadFile(): void {
    const cached = this.blobUrl();
    if (cached) {
      this.triggerDownload(cached);
      return;
    }
    this.pendingDownload = true;
    this.loadBlob();
  }

  // loadBlob 成功后若有挂起的下载请求，触发一次下载。
  private flushPendingDownload(url: string): void {
    if (!this.pendingDownload) return;
    this.pendingDownload = false;
    this.triggerDownload(url);
  }

  // 用隐藏 <a download> 触发浏览器下载。url 是组件持有的 blob: 缓存（ngOnDestroy 统一回收），
  // 这里不 revoke——它仍可能用于「原文件」Tab 预览的同一 URL。
  private triggerDownload(url: string): void {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download =
      this.document()?.fileOrigin?.originalFileName || this.document()?.title || 'document';
    anchor.click();
  }

  ngOnDestroy(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
  }

  // <img> 渲染失败（blob 已下载但解码失败）——并入统一预览错误信号。
  onPreviewError(): void {
    this.previewError.set(true);
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

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
import { escapeHtmlChars, ListService, LocalizationPipe, PermissionService } from '@abp/ng.core';
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
import { map, of } from 'rxjs';
import {
  CreateDocumentTypeDto,
  DocumentTypeDto,
  DocumentTypeService,
  PAPERBASE_PERMISSIONS,
  SlugSuggestionService,
} from '@dignite/paperbase';
import {
  ClientPagedResult,
  configureEntityTable,
  pageClientItems,
  PAPERBASE_TABLES,
  SortAccessors,
} from '../../shared/extensible-table';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';
import { FieldReextractionModalComponent } from '../../reprocessing/field-reextraction-modal/field-reextraction-modal.component';
import { ReclassificationModalComponent } from '../../reprocessing/reclassification-modal/reclassification-modal.component';

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
};

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
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: PAPERBASE_TABLES.DocumentTypes,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentTypeListComponent implements OnInit {
  private readonly service = inject(DocumentTypeService);
  private readonly slugService = inject(SlugSuggestionService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);

  readonly list = inject(ListService);

  // Create/edit/delete buttons require any DocumentTypes write grant (#217); the route's
  // DocumentTypes.Default only lists. ABP evaluates the `||` policy expression.
  readonly canManage = this.permissionService.getGrantedPolicy(
    `${PAPERBASE_PERMISSIONS.DocumentTypes.Create} || ${PAPERBASE_PERMISSIONS.DocumentTypes.Update} || ${PAPERBASE_PERMISSIONS.DocumentTypes.Delete}`,
  );

  // 批量重处理入口（#289）——admin 级、独立于类型 CRUD 权限。
  readonly canReextractFields = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Reprocessing.FieldExtraction,
  );
  readonly canReclassify = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Reprocessing.Reclassification,
  );

  // 打开的重处理模态目标（null = 关闭）。
  reextractTarget = signal<DocumentTypeDto | null>(null);
  reclassifyTarget = signal<DocumentTypeDto | null>(null);

  allTypes = signal<DocumentTypeDto[]>([]);
  types = signal<ClientPagedResult<DocumentTypeDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  showDeleted = signal(false);

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
    // 可选分类辅助说明（#262）：仅帮助 AI 识别类型，不参与文档内容加工。
    description: ['', [Validators.maxLength(MAX_DESCRIPTION_LENGTH)]],
    confidenceThreshold: [0.7, [Validators.required, Validators.min(0), Validators.max(1)]],
    priority: [0, [Validators.required]],
  });

  constructor() {
    configureEntityTable<DocumentTypeDto>(this.extensions, PAPERBASE_TABLES.DocumentTypes, [
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

  // LLM 不可用 / 未翻译时的本地回退：取与现有类型代码不冲突的最小 type_{n}。
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
    this.form.reset({ typeCode: '', displayName: '', description: '', confidenceThreshold: 0.7, priority: 0 });
    this.form.controls.typeCode.enable();
    // 必须在 form.reset()/enable() 之后调用：二者触发的 valueChanges 会误标"手动编辑"，
    // reset() 清掉该标记并复位建议状态（含 spinner）。
    this.slugHandle?.reset();
    this.editing.set('create');
  }

  openEdit(type: DocumentTypeDto): void {
    // 先 disable 再 reset：让 slug 自动建议在编辑态 reset 期间识别为非自动接管，
    // 不会把既有 typeCode 当"过期键"清空（见 wireSlugSuggestion 注释）。
    this.form.controls.typeCode.disable();
    this.form.reset({
      typeCode: type.typeCode,
      displayName: type.displayName,
      description: type.description ?? '',
      confidenceThreshold: type.confidenceThreshold,
      priority: type.priority,
    });
    this.form.controls.typeCode.enable();
    this.slugHandle?.markManual();
    this.editing.set(type);
  }

  // 显示名失焦 → 触发 slug 自动建议（实测反馈：从停顿防抖改为失焦触发）。
  onDisplayNameBlur(): void {
    this.slugHandle?.notifyDisplayNameBlur();
  }

  // 遮罩关闭防误触：只有当 mousedown 与 click 都发生在遮罩本身（而非对话框内）时才关闭。
  // 否则在输入框里拖选文本、松手落在遮罩区时，浏览器会在遮罩上触发 click（mousedown/mouseup 的最近公共祖先），
  // 误关弹窗并丢失已填内容。记录 mousedown 起点是判定"这一次点击是否真的从遮罩发起"的唯一可靠方式。
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

    this.isSubmitting.set(true);
    const raw = this.form.getRawValue();

    if (mode === 'create') {
      const input: CreateDocumentTypeDto = {
        typeCode: raw.typeCode,
        displayName: raw.displayName,
        description: raw.description.trim() || undefined,
        confidenceThreshold: raw.confidenceThreshold,
        priority: raw.priority,
      };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::DocumentType:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, {
        typeCode: raw.typeCode,
        displayName: raw.displayName,
        description: raw.description.trim() || undefined,
        confidenceThreshold: raw.confidenceThreshold,
        priority: raw.priority,
      })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::DocumentType:UpdatedSuccessfully'),
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
              this.load();
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
          this.load();
        },
      });
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
}

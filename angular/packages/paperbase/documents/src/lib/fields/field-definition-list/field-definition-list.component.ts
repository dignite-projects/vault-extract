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
import { ActivatedRoute, Router } from '@angular/router';
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
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { map, of, Subject, takeUntil } from 'rxjs';
import {
  CreateFieldDefinitionDto,
  DocumentTypeService,
  FieldDataType,
  FieldDefinitionDraftDto,
  FieldDefinitionDto,
  FieldDefinitionService,
  FieldDraftSuggestionService,
  fieldDataTypeOptions,
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
import { FieldReextractionModalComponent } from '../../reprocessing/field-reextraction-modal/field-reextraction-modal.component';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';

// Mirrors FieldDefinitionConsts (Domain.Shared): Name whitelist + length caps.
const NAME_PATTERN = /^[A-Za-z0-9_\-]{1,64}$/;
const MAX_NAME_LENGTH = 64;
const MAX_DISPLAY_NAME_LENGTH = 128;
const MAX_PROMPT_LENGTH = 1024;

const FIELD_DEFINITION_SORTS: SortAccessors<FieldDefinitionDto> = {
  displayOrder: field => field.displayOrder,
  name: field => field.name,
  displayName: field => field.displayName,
  dataType: field => field.dataType,
  isRequired: field => field.isRequired,
  prompt: field => field.prompt,
};

@Component({
  selector: 'lib-field-definition-list',
  templateUrl: './field-definition-list.component.html',
  styleUrls: ['./field-definition-list.component.scss'],
  imports: [
    CommonModule,
    ReactiveFormsModule,
    LocalizationPipe,
    ExtensibleTableComponent,
    NgbDropdownModule,
    FieldReextractionModalComponent,
  ],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: PAPERBASE_TABLES.FieldDefinitions,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FieldDefinitionListComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(FieldDefinitionService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly slugService = inject(SlugSuggestionService);
  private readonly draftService = inject(FieldDraftSuggestionService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);

  readonly list = inject(ListService);

  // Create/edit/delete buttons require any FieldDefinitions write grant (#217); the route's
  // FieldDefinitions.Default only lists. ABP evaluates the `||` policy expression.
  readonly canManage = this.permissionService.getGrantedPolicy(
    `${PAPERBASE_PERMISSIONS.FieldDefinitions.Create} || ${PAPERBASE_PERMISSIONS.FieldDefinitions.Update} || ${PAPERBASE_PERMISSIONS.FieldDefinitions.Delete}`,
  );
  // 批量字段重抽入口（#289）——admin 级、独立于字段 CRUD 权限。
  readonly canReextractFields = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Reprocessing.FieldExtraction,
  );
  // null/false = 关闭重抽模态。
  showReextract = signal(false);
  readonly dataTypeOptions = fieldDataTypeOptions;
  readonly FieldDataType = FieldDataType;

  // 路由按不可变 DocumentTypeId 绑定（#207）；header 徽标主显示用户友好的 DisplayName（#261），
  // TypeCode 降为 hover 提示——二者均由当前层可见类型按 Id 即时解析（穿透重命名）。
  documentTypeId = '';
  documentTypeDisplayName = signal('');
  documentTypeCode = signal('');
  allFields = signal<FieldDefinitionDto[]>([]);
  fields = signal<ClientPagedResult<FieldDefinitionDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  showDeleted = signal(false);

  editing = signal<FieldDefinitionDto | 'create' | null>(null);
  isSubmitting = signal(false);
  isSuggesting = signal(false);
  // #264：「按提示词起草」进行中 / 刚完成一次起草（驱动 spinner 与「请核对草稿」提示）。
  isDrafting = signal(false);
  justDrafted = signal(false);

  private slugHandle?: SlugSuggestionHandle;
  private tableQuery: Partial<ABP.PageQueryParams> = {};

  // #264：取消在飞起草请求的信号。关闭弹窗时 emit，避免迟到的草稿覆盖重新打开的（无关字段）表单。
  // 组件级 destroyRef 不随弹窗关闭触发（弹窗只是 editing=null，组件不销毁），故需要独立的 per-modal 取消闸门。
  private readonly draftCancelled$ = new Subject<void>();

  readonly form = this.fb.nonNullable.group({
    name: [
      '',
      [Validators.required, Validators.maxLength(MAX_NAME_LENGTH), Validators.pattern(NAME_PATTERN)],
    ],
    displayName: ['', [Validators.required, Validators.maxLength(MAX_DISPLAY_NAME_LENGTH)]],
    // 抽取指令选填（实测反馈）：去掉 Validators.required，仅保留长度上限；留空时后端 NormalizePrompt 收敛为 null。
    prompt: ['', [Validators.maxLength(MAX_PROMPT_LENGTH)]],
    dataType: [FieldDataType.Text, [Validators.required]],
    displayOrder: [0, [Validators.required]],
    isRequired: [false],
    // #212：多值仅文本有效（镜像后端 FieldDefinition.ValidateMultiValue 不变量）。
    // 非文本时由 applyAllowMultiplePolicy 强制置 false 并 disable，提交前 getRawValue 仍带回 false。
    allowMultiple: [false],
  });

  // 驱动模板：dataType === Text 时才允许勾选"多值"。
  readonly isTextType = signal(true);

  constructor() {
    configureEntityTable<FieldDefinitionDto>(this.extensions, PAPERBASE_TABLES.FieldDefinitions, [
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.Number,
        name: 'displayOrder',
        displayName: '::FieldDefinition:DisplayOrder',
        sortable: true,
        columnWidth: 120,
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'name',
        displayName: '::FieldDefinition:Name',
        sortable: true,
        columnWidth: 180,
        valueResolver: data => of(`<code>${escapeHtmlChars(data.record.name)}</code>`),
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'displayName',
        displayName: '::FieldDefinition:DisplayName',
        sortable: true,
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'dataType',
        displayName: '::FieldDefinition:DataType',
        sortable: true,
        columnWidth: 170,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          const label = fieldDataTypeOptions.find(o => o.value === data.record.dataType)?.key ?? String(data.record.dataType);
          const suffix = data.record.allowMultiple ? '[]' : '';
          return of(`<span class="badge bg-light text-dark border">${escapeHtmlChars(localization.instant(label))}${suffix}</span>`);
        },
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'isRequired',
        displayName: '::FieldDefinition:Required',
        sortable: true,
        columnWidth: 150,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          return of(data.record.isRequired
            ? `<span class="badge bg-warning text-dark">${escapeHtmlChars(localization.instant('::FieldDefinition:Required'))}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'prompt',
        displayName: '::FieldDefinition:Prompt',
        sortable: true,
        columnWidth: 320,
        valueResolver: data => {
          const prompt = data.record.prompt;
          return of(prompt
            ? `<span class="d-inline-block text-truncate" style="max-width:280px" title="${escapeHtmlChars(prompt)}">${escapeHtmlChars(prompt)}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
    ]);
  }

  ngOnInit(): void {
    this.hookTableQuery();
    this.documentTypeId = this.route.snapshot.paramMap.get('typeId') ?? '';
    this.resolveDocumentType();
    this.slugHandle = wireSlugSuggestion({
      displayName: this.form.controls.displayName,
      target: this.form.controls.name,
      suggest: text => this.slugService.suggest({ label: text }, undefined).pipe(map(r => r.slug ?? '')),
      fallback: () => this.nextFieldSlug(),
      destroyRef: this.destroyRef,
      onPending: pending => this.isSuggesting.set(pending),
    });
    // #212：dataType 变化时实时套用"多值仅文本"策略（镜像后端不变量，避免提交非法组合被后端 loud fail）。
    this.form.controls.dataType.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(dataType => this.applyAllowMultiplePolicy(dataType));
    this.load();
  }

  // 非文本字段强制 allowMultiple=false 且禁用勾选框；切回文本时重新启用（保留当前值）。
  // 仅文本 + 多值是后端实体层允许的组合（FieldDefinition.MultiValueRequiresStringType），客户端镜像该约束做 UX 防呆。
  private applyAllowMultiplePolicy(dataType: FieldDataType): void {
    const isText = dataType === FieldDataType.Text;
    this.isTextType.set(isText);
    const control = this.form.controls.allowMultiple;
    if (isText) {
      control.enable({ emitEvent: false });
    } else {
      control.setValue(false, { emitEvent: false });
      control.disable({ emitEvent: false });
    }
  }

  // header 徽标展示用：按不可变 Id 在当前层可见类型里解析当前类型，主显示 DisplayName、TypeCode 作 hover 提示（穿透重命名）。
  private resolveDocumentType(): void {
    this.documentTypeService.getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => {
          const type = types.find(t => t.id === this.documentTypeId);
          this.documentTypeDisplayName.set(type?.displayName ?? '');
          this.documentTypeCode.set(type?.typeCode ?? '');
        },
      });
  }

  // LLM 不可用 / 未翻译时的本地回退：取与现有字段名不冲突的最小 field_{n}。
  private nextFieldSlug(): string {
    const existing = new Set(this.allFields().map(f => f.name));
    let i = 1;
    while (existing.has(`field_${i}`)) i++;
    return `field_${i}`;
  }

  refresh(): void {
    this.load();
  }

  toggleDeleted(): void {
    this.showDeleted.update(v => !v);
    this.load();
  }

  goBack(): void {
    this.router.navigate(['/documents/types']);
  }

  openReextractFields(): void {
    if (this.documentTypeId) {
      this.showReextract.set(true);
    }
  }

  private load(): void {
    this.isLoading.set(true);
    const source$ = this.service.getList({
      documentTypeId: this.documentTypeId,
      onlyDeleted: this.showDeleted(),
    });
    source$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.allFields.set([...list].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)));
        this.list.totalCount = list.length;
        this.applyTableQuery();
        this.isLoading.set(false);
      },
      error: () => {
        this.allFields.set([]);
        this.fields.set({ totalCount: 0, items: [] });
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
    this.fields.set(pageClientItems(this.allFields(), query, FIELD_DEFINITION_SORTS));
  }

  openCreate(): void {
    const nextOrder = this.allFields().reduce((max, f) => Math.max(max, f.displayOrder ?? 0), -1) + 1;
    this.form.reset({
      name: '',
      displayName: '',
      prompt: '',
      dataType: FieldDataType.Text,
      displayOrder: nextOrder,
      isRequired: false,
      allowMultiple: false,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(FieldDataType.Text);
    // 必须在 form.reset()/enable() 之后调用：二者触发的 valueChanges 会误标"手动编辑"，
    // reset() 清掉该标记并复位建议状态（含 spinner）。
    this.slugHandle?.reset();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.editing.set('create');
  }

  openEdit(field: FieldDefinitionDto): void {
    // 先 disable 再 reset：让 slug 自动建议在编辑态 reset 期间识别为非自动接管，
    // 不会把既有 name 当"过期键"清空（见 wireSlugSuggestion 注释）。
    this.form.controls.name.disable();
    this.form.reset({
      name: field.name,
      displayName: field.displayName,
      prompt: field.prompt ?? '',
      dataType: field.dataType,
      displayOrder: field.displayOrder,
      isRequired: field.isRequired,
      allowMultiple: field.allowMultiple,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(field.dataType ?? FieldDataType.Text);
    this.slugHandle?.markManual();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.editing.set(field);
  }

  // #264：按提示词起草字段元数据。提示词为主输入，一次 LLM 调用起草其余字段，整组覆盖后用户可逐项核对 / 修改。
  draft(): void {
    const prompt = (this.form.controls.prompt.value ?? '').trim();
    if (!prompt || this.isDrafting()) return;
    // forNewField 控制后端是否额外建议机器键 Name：编辑既有字段时 Name 是契约级冻结身份键，不被起草覆盖（护栏 1）。
    const forNewField = this.editing() === 'create';
    this.isDrafting.set(true);
    this.draftService.draft({ prompt, forNewField }, undefined)
      // takeUntil(draftCancelled$)：关闭弹窗即取消，迟到响应不写入新表单（#264 review #1）。
      .pipe(takeUntil(this.draftCancelled$), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: draft => {
          this.applyDraft(draft, forNewField);
          this.isDrafting.set(false);
        },
        error: () => {
          this.isDrafting.set(false);
          // 本次未产出草稿——复位「请核对草稿」横幅，避免与「无法起草」提示同屏矛盾（#264 review2 #1，对齐空草稿分支）。
          this.justDrafted.set(false);
          this.toaster.warn('::FieldDefinition:DraftUnavailable', '::Warning');
        },
      });
  }

  // 整组覆盖对应控件（issue #264 确认的落入方式）。emitEvent:false 避免触发 displayName→slug 接线把刚起草的 name 清空。
  private applyDraft(draft: FieldDefinitionDraftDto, forNewField: boolean): void {
    // 后端起草失败 / 超时回退保守空草稿——DisplayName 为空即判定不可用：保留用户已填内容，仅提示手填，不覆盖。
    if (!draft.displayName) {
      // 复位「请核对草稿」横幅：本次未产出草稿，避免上一次成功的横幅与「无法起草」提示自相矛盾（#264 review #6）。
      this.justDrafted.set(false);
      this.toaster.info('::FieldDefinition:DraftUnavailable', '::Info');
      return;
    }
    const dataType = draft.dataType ?? FieldDataType.Text;
    this.form.controls.displayName.setValue(draft.displayName, { emitEvent: false });
    this.form.controls.dataType.setValue(dataType, { emitEvent: false });
    this.form.controls.isRequired.setValue(draft.isRequired ?? false, { emitEvent: false });
    // setValue(dataType) 用了 emitEvent:false，valueChanges 不触发，需手动套用「多值仅文本」策略（启/禁用勾选框）。
    this.applyAllowMultiplePolicy(dataType);
    this.form.controls.allowMultiple.setValue(
      dataType === FieldDataType.Text && (draft.allowMultiple ?? false),
      { emitEvent: false },
    );
    if (forNewField) {
      // 新建：整组覆盖机器键——用建议值，缺失（如纯 CJK 未翻译 sanitize 成空）时回退本地占位 field_{n}，
      // 绝不残留基于「上一个显示名」的过期键（#264 review #2）；并标记手动保留，后续 displayName 失焦不再用 slug 覆盖
      // 这个已起草/已核对的键（用户仍可手改 name）。
      this.form.controls.name.setValue(draft.name || this.nextFieldSlug(), { emitEvent: false });
      this.slugHandle?.markManual();
    }
    this.form.markAsDirty();
    this.justDrafted.set(true);
  }

  // 显示名失焦 → 触发 slug 自动建议（实测反馈：从停顿防抖改为失焦触发）。
  onDisplayNameBlur(): void {
    // 起草在飞时不触发失焦 slug 路径：否则两条 LLM 响应竞争写 name，最后落地者随机（#264 review #2）。
    // 起草本身会整组覆盖并 markManual name，无需失焦路径再补。
    if (this.isDrafting()) return;
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
    // 取消任何在飞起草请求 + 清 spinner，避免迟到草稿污染下次打开的表单、或留下永久禁用的起草按钮（#264 review #1）。
    this.draftCancelled$.next();
    this.isDrafting.set(false);
    this.justDrafted.set(false);
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
      const input: CreateFieldDefinitionDto = {
        documentTypeId: this.documentTypeId,
        name: raw.name,
        displayName: raw.displayName,
        prompt: raw.prompt,
        dataType: raw.dataType,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        // 非文本时 control 被 disable，但 getRawValue 仍带回（已被策略置 false）。
        allowMultiple: raw.allowMultiple,
      };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::FieldDefinition:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, {
        name: raw.name,
        displayName: raw.displayName,
        prompt: raw.prompt,
        dataType: raw.dataType,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        allowMultiple: raw.allowMultiple,
      })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::FieldDefinition:UpdatedSuccessfully'),
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

  delete(field: FieldDefinitionDto): void {
    this.confirmation
      .warn('::FieldDefinition:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(field.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::FieldDefinition:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::FieldDefinition:DeleteFailed', '::Error'),
          });
      });
  }

  restore(field: FieldDefinitionDto): void {
    this.service.restore(field.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.toaster.success('::FieldDefinition:RestoredSuccessfully', '::Success');
          this.load();
        },
      });
  }

  dataTypeLabel(dataType: FieldDataType | undefined): string {
    return this.dataTypeOptions.find(o => o.value === dataType)?.key ?? String(dataType);
  }
}

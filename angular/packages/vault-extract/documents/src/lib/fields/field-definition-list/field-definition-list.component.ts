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
  EXTRACT_PERMISSIONS,
  SlugSuggestionService,
} from '@dignite/vault-extract';
import {
  ClientPagedResult,
  configureEntityTable,
  pageClientItems,
  EXTRACT_TABLES,
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
      useValue: EXTRACT_TABLES.FieldDefinitions,
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
    `${EXTRACT_PERMISSIONS.FieldDefinitions.Create} || ${EXTRACT_PERMISSIONS.FieldDefinitions.Update} || ${EXTRACT_PERMISSIONS.FieldDefinitions.Delete}`,
  );
  // Bulk field re-extraction entry point (#289): admin-level and independent from field CRUD
  // permissions.
  readonly canReextractFields = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Reprocessing.FieldExtraction,
  );
  // null/false means the re-extraction modal is closed.
  showReextract = signal(false);
  readonly dataTypeOptions = fieldDataTypeOptions;
  readonly FieldDataType = FieldDataType;

  // Route binding uses immutable DocumentTypeId (#207). The header badge primarily shows the
  // user-friendly DisplayName (#261), while TypeCode is demoted to hover text. Both are resolved by id
  // from types visible in the current layer, so renames are pierced.
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
  // #264: "draft from prompt" is in progress / just completed once. Drives the spinner and "review the
  // draft" notice.
  isDrafting = signal(false);
  justDrafted = signal(false);

  private slugHandle?: SlugSuggestionHandle;
  private tableQuery: Partial<ABP.PageQueryParams> = {};

  // #264: signal that cancels in-flight draft requests. Emit when closing the modal so a late draft does
  // not overwrite a reopened form for an unrelated field.
  // The component-level destroyRef does not fire when the modal closes, because the modal only sets
  // editing=null and the component is not destroyed. Therefore a separate per-modal cancellation gate is
  // needed.
  private readonly draftCancelled$ = new Subject<void>();

  readonly form = this.fb.nonNullable.group({
    name: [
      '',
      [Validators.required, Validators.maxLength(MAX_NAME_LENGTH), Validators.pattern(NAME_PATTERN)],
    ],
    displayName: ['', [Validators.required, Validators.maxLength(MAX_DISPLAY_NAME_LENGTH)]],
    // Extraction instruction is optional based on measured feedback: remove Validators.required and keep
    // only the length cap. Backend NormalizePrompt converges blank values to null.
    prompt: ['', [Validators.maxLength(MAX_PROMPT_LENGTH)]],
    dataType: [FieldDataType.Text, [Validators.required]],
    displayOrder: [0, [Validators.required]],
    isRequired: [false],
    // #212: multiple values are valid only for text, mirroring the backend
    // FieldDefinition.ValidateMultiValue invariant. For non-text fields, applyAllowMultiplePolicy forces
    // false and disables the control; getRawValue still returns false before submit.
    allowMultiple: [false],
    // #411: whether this field participates in the type's duplicate-detection unique key.
    isUniqueKey: [false],
  });

  // Drives the template: allow checking "multiple values" only when dataType === Text.
  readonly isTextType = signal(true);

  constructor() {
    configureEntityTable<FieldDefinitionDto>(this.extensions, EXTRACT_TABLES.FieldDefinitions, [
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
    // #212: apply the "multiple values only for text" policy whenever dataType changes, mirroring the
    // backend invariant and preventing illegal combinations from failing loudly on submit.
    this.form.controls.dataType.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(dataType => this.applyAllowMultiplePolicy(dataType));
    this.load();
  }

  // Non-text fields force allowMultiple=false and disable the checkbox. Switching back to text re-enables
  // it while preserving the current value.
  // Text plus multiple values is the only combination allowed by the backend entity layer
  // (FieldDefinition.MultiValueRequiresStringType), and the client mirrors that constraint for UX
  // guardrails.
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

  // For the header badge: resolve the current type by immutable id from types visible in the current
  // layer. DisplayName is primary, and TypeCode is hover text, piercing renames.
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

  // Local fallback when the LLM is unavailable or does not translate: choose the smallest field_{n} that
  // does not conflict with existing field names.
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
      isUniqueKey: false,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(FieldDataType.Text);
    // Must be called after form.reset()/enable(): both trigger valueChanges that can be misread as
    // "manual edit". reset() clears that marker and resets suggestion state, including the spinner.
    this.slugHandle?.reset();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.editing.set('create');
  }

  openEdit(field: FieldDefinitionDto): void {
    // Disable before reset so slug auto-suggestion sees edit-mode reset as not automatically managed and
    // does not clear the existing name as a stale key. See wireSlugSuggestion comments.
    this.form.controls.name.disable();
    this.form.reset({
      name: field.name,
      displayName: field.displayName,
      prompt: field.prompt ?? '',
      dataType: field.dataType,
      displayOrder: field.displayOrder,
      isRequired: field.isRequired,
      allowMultiple: field.allowMultiple,
      isUniqueKey: field.isUniqueKey ?? false,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(field.dataType ?? FieldDataType.Text);
    this.slugHandle?.markManual();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.editing.set(field);
  }

  // #264: draft field metadata from the prompt. The prompt is the primary input; one LLM call drafts the
  // remaining fields, applies them as a group, and lets the user review or modify each item.
  draft(): void {
    const prompt = (this.form.controls.prompt.value ?? '').trim();
    if (!prompt || this.isDrafting()) return;
    // forNewField controls whether the backend also suggests the machine key Name. When editing an
    // existing field, Name is a contract-level frozen identity key and is not overwritten by drafting
    // (guardrail 1).
    const forNewField = this.editing() === 'create';
    this.isDrafting.set(true);
    this.draftService.draft({ prompt, forNewField }, undefined)
      // takeUntil(draftCancelled$): cancel when the modal closes, so late responses do not write into a
      // new form (#264 review #1).
      .pipe(takeUntil(this.draftCancelled$), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: draft => {
          this.applyDraft(draft, forNewField);
          this.isDrafting.set(false);
        },
        error: () => {
          this.isDrafting.set(false);
          // No draft was produced this time. Reset the "review draft" banner to avoid contradicting the
          // "draft unavailable" hint on the same screen (#264 review2 #1, aligned with the empty-draft
          // branch).
          this.justDrafted.set(false);
          this.toaster.warn('::FieldDefinition:DraftUnavailable', '::Warning');
        },
      });
  }

  // Apply the corresponding controls as a group, the landing behavior confirmed in issue #264.
  // emitEvent:false avoids triggering the displayName-to-slug wiring and clearing the just-drafted name.
  private applyDraft(draft: FieldDefinitionDraftDto, forNewField: boolean): void {
    // Backend draft failure or timeout falls back to a conservative empty draft. Empty DisplayName means
    // unavailable: keep user-entered content, show a manual-entry hint, and do not overwrite.
    if (!draft.displayName) {
      // Reset the "review draft" banner: this run produced no draft, avoiding a contradiction between a
      // previous success banner and the "draft unavailable" hint (#264 review #6).
      this.justDrafted.set(false);
      this.toaster.info('::FieldDefinition:DraftUnavailable', '::Info');
      return;
    }
    const dataType = draft.dataType ?? FieldDataType.Text;
    this.form.controls.displayName.setValue(draft.displayName, { emitEvent: false });
    this.form.controls.dataType.setValue(dataType, { emitEvent: false });
    this.form.controls.isRequired.setValue(draft.isRequired ?? false, { emitEvent: false });
    // setValue(dataType) used emitEvent:false, so valueChanges does not fire; manually apply the
    // "multiple values only for text" policy to enable/disable the checkbox.
    this.applyAllowMultiplePolicy(dataType);
    this.form.controls.allowMultiple.setValue(
      dataType === FieldDataType.Text && (draft.allowMultiple ?? false),
      { emitEvent: false },
    );
    if (forNewField) {
      // Create mode: overwrite the machine key as part of the group. Use the suggested value, or fall
      // back to local placeholder field_{n} when missing, such as when pure CJK sanitizes to empty after
      // no translation.
      // Never leave behind a stale key based on the previous display name (#264 review #2). Mark it as
      // manually retained so later displayName blur does not overwrite this drafted/reviewed key with a
      // slug; the user may still edit name manually.
      this.form.controls.name.setValue(draft.name || this.nextFieldSlug(), { emitEvent: false });
      this.slugHandle?.markManual();
    }
    this.form.markAsDirty();
    this.justDrafted.set(true);
  }

  // Display-name blur triggers slug auto-suggestion. Measured feedback changed this from pause debounce
  // to blur trigger.
  onDisplayNameBlur(): void {
    // Do not trigger the blur slug path while drafting is in flight; otherwise two LLM responses compete
    // to write name, and the last landing response is random (#264 review #2).
    // Drafting itself applies the group and markManual name, so the blur path does not need to supplement it.
    if (this.isDrafting()) return;
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
    // Cancel any in-flight draft request and clear the spinner, preventing late drafts from contaminating
    // the next opened form or leaving the draft button permanently disabled (#264 review #1).
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
        // For non-text fields the control is disabled, but getRawValue still carries it back after the
        // policy has set it to false.
        allowMultiple: raw.allowMultiple,
        isUniqueKey: raw.isUniqueKey,
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
        isUniqueKey: raw.isUniqueKey,
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

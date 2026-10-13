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
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
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
import { marked } from 'marked';
import { FieldTypeDefinition, FieldTypeResolver, FlexFieldConfigComponent, FlexFieldData } from '@dignite/ng.flex-fields';
import {
  CreateFieldDefinitionDto,
  DocumentTypeService,
  FieldDefinitionDraftDto,
  FieldDefinitionDto,
  FieldDefinitionService,
  FieldDraftSuggestionService,
  FieldPromptPolishService,
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
import { FieldReextractionModalComponent } from '../../reprocessing/field-reextraction-modal/field-reextraction-modal.component';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';

// Mirrors FieldDefinitionConsts (Domain.Shared): Name whitelist + length caps.
// #447: Prompt has no length cap — it is admin-authored Markdown configuration, persisted uncapped.
const NAME_PATTERN = /^[A-Za-z0-9_\-]{1,64}$/;
const MAX_NAME_LENGTH = 64;
const MAX_DISPLAY_NAME_LENGTH = 128;

const FIELD_DEFINITION_SORTS: SortAccessors<FieldDefinitionDto> = {
  displayOrder: field => field.displayOrder,
  name: field => field.name,
  displayName: field => field.displayName,
  fieldTypeName: field => field.fieldTypeName,
  isRequired: field => field.isRequired,
  description: field => field.description,
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
    FlexFieldConfigComponent,
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
  private readonly polishService = inject(FieldPromptPolishService);
  private readonly fieldTypeResolver = inject(FieldTypeResolver);
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
  // #447: AI-polish (rewrite the prompt itself into clean Markdown) in progress, and Edit/Preview toggle
  // for the Markdown prompt editor.
  isPolishing = signal(false);
  showPromptPreview = signal(false);
  // #447: memoize the rendered Markdown preview so change-detection cycles while the Preview pane is open
  // don't re-run marked.parse; keyed on the raw prompt string (null = nothing rendered yet).
  private promptPreviewSource: string | null = null;
  private promptPreviewCache = '';

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
    // Extraction instruction is optional (measured feedback: no Validators.required). #447: no maxLength -
    // it is admin-authored Markdown configuration, persisted uncapped. The server converges blank to null.
    // Named `description` since #559: it maps to the FlexFields contract member of that name.
    description: [''],
    fieldTypeName: ['', [Validators.required]],
    displayOrder: [0, [Validators.required]],
    isRequired: [false],
    // #559: whether this field's values reach the query index, and are therefore filterable. Meaningless
    // for a type that indexes nothing, where applySearchableAvailability disables the control.
    isSearchable: [true],
    // #411: whether this field participates in the type's duplicate-detection unique key.
    isUniqueKey: [false],
  });

  // The field-type picker's vocabulary: every type flex-fields has registered (built-ins + bolt-ons)
  // that this deployment's backend also supports (VaultExtractFieldTypes.SupportedFieldTypeNames, via
  // GetFieldTypesAsync — the kernel registers Tree unconditionally as a built-in, but nothing in this
  // app's read/write/schema pipeline supports it). Populated once that request lands; see the
  // constructor.
  readonly fieldTypes = signal<readonly FieldTypeDefinition[]>([]);

  // Seeds <ff-flex-field-config>'s "selected" input: the stored field being edited (so it restores
  // saved configuration values), or a synthetic draft result (so an AI-drafted type suggestion — a
  // date-only DateTime, a Tags MaxCount — carries its suggested configuration too), or undefined for a
  // plain new field.
  readonly configSeed = signal<FlexFieldData | undefined>(undefined);

  // Which field types can be searched at all, keyed by registration name - straight from the server, via
  // FieldDefinitionAppService.GetFieldTypesAsync. It has to come from there: the answer is
  // IFieldType.IndexValueType, and the Angular library deliberately does not restate it (see
  // FieldTypeDefinition's doc). Empty until the request lands, which reads as "no restriction" - the
  // server rejects the combination anyway, so a slow response can at worst let a save fail loudly.
  private indexableByFieldType = new Map<string, boolean>();

  // Drives the template: whether the currently selected field type rules out searching. Disables the
  // "可筛选" checkbox and swaps in the hint that explains why.
  readonly searchableUnsupported = signal(false);

  // Same gate as searchableUnsupported (same map, same predicate) but for the duplicate-detection
  // unique key: a type with no query-index slot cannot back IsUniqueKey either (#626 backend guard).
  // Drives the template: disables the "唯一键" checkbox and swaps in the hint that explains why.
  readonly uniqueKeyUnsupported = signal(false);

  constructor() {
    configureEntityTable<FieldDefinitionDto>(this.extensions, EXTRACT_TABLES.FieldDefinitions, [
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.Number,
        name: 'displayOrder',
        displayName: 'VaultExtract::FieldDefinition:DisplayOrder',
        sortable: true,
        columnWidth: 120,
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'name',
        displayName: 'VaultExtract::FieldDefinition:Name',
        sortable: true,
        columnWidth: 180,
        valueResolver: data => of(`<code>${escapeHtmlChars(data.record.name)}</code>`),
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'displayName',
        displayName: 'VaultExtract::FieldDefinition:DisplayName',
        sortable: true,
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'fieldTypeName',
        displayName: 'VaultExtract::FieldDefinition:FieldType',
        sortable: true,
        columnWidth: 170,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          const fieldType = this.fieldTypeResolver.find(data.record.fieldTypeName ?? '');
          // An unknown key still renders, as itself: a field can name a type this deployment no longer
          // registers, and showing the raw key is what lets an admin see why it stopped working.
          const label = fieldType
            ? localization.instant(fieldType.displayNameKey)
            : (data.record.fieldTypeName ?? '');
          return of(`<span class="badge bg-light text-dark border">${escapeHtmlChars(label)}</span>`);
        },
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'isRequired',
        displayName: 'VaultExtract::FieldDefinition:Required',
        sortable: true,
        columnWidth: 150,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          return of(data.record.isRequired
            ? `<span class="badge bg-warning text-dark">${escapeHtmlChars(localization.instant('VaultExtract::FieldDefinition:Required'))}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'isUniqueKey',
        displayName: 'VaultExtract::FieldDefinition:IsUniqueKey',
        sortable: true,
        columnWidth: 150,
        valueResolver: data => {
          const localization = data.getInjected(LocalizationService);
          return of(data.record.isUniqueKey
            ? `<span class="badge bg-primary">${escapeHtmlChars(localization.instant('VaultExtract::FieldDefinition:IsUniqueKey'))}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
      EntityProp.create<FieldDefinitionDto>({
        type: ePropType.String,
        name: 'description',
        displayName: 'VaultExtract::FieldDefinition:Prompt',
        sortable: true,
        columnWidth: 320,
        valueResolver: data => {
          const prompt = data.record.description;
          return of(prompt
            ? `<span class="d-inline-block text-truncate" style="max-width:280px" title="${escapeHtmlChars(prompt)}">${escapeHtmlChars(prompt)}</span>`
            : '<span class="text-muted">-</span>');
        },
      }),
    ]);

    this.service.getFieldTypes()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(fieldTypes => {
        const supported = new Set(fieldTypes.map(fieldType => fieldType.name ?? ''));
        this.fieldTypes.set(this.fieldTypeResolver.getAll().filter(fieldType => supported.has(fieldType.name)));
        this.indexableByFieldType = new Map(
          fieldTypes.map(fieldType => [fieldType.name ?? '', fieldType.indexable ?? true]),
        );
        // A modal opened before this landed was built against an empty map/list - re-apply so it
        // catches up.
        this.applySearchableAvailability(this.form.controls.fieldTypeName.value);
        this.applyUniqueKeyAvailability(this.form.controls.fieldTypeName.value);
      });
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
    // Changing the field type replaces the configuration panel wholesale (<ff-flex-field-config> swaps
    // in a fresh configComponent and rebuilds the "configuration" form group from scratch): carrying a
    // previously-seeded selection across would let it resurrect a stale type's values on switching back.
    this.form.controls.fieldTypeName.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(fieldTypeName => {
        this.configSeed.set(undefined);
        this.applySearchableAvailability(fieldTypeName);
        this.applyUniqueKeyAvailability(fieldTypeName);
      });
    this.load();
  }

  /**
   * A field type with no query-index slot cannot actually be searched however the switch is set, so the
   * switch is pinned off and disabled rather than left as a control with no effect - the hint beside it
   * says why. getRawValue still returns the (forced-false) value.
   */
  private applySearchableAvailability(fieldTypeName?: string): void {
    this.searchableUnsupported.set(
      !!fieldTypeName && this.indexableByFieldType.get(fieldTypeName) === false,
    );
    const control = this.form.controls.isSearchable;
    if (this.searchableUnsupported()) {
      control.setValue(false, { emitEvent: false });
      control.disable({ emitEvent: false });
    } else if (control.disabled) {
      // Restore the default now that the switch is meaningful again - it was pinned to false and
      // locked by a previous, non-indexable type selection, not by the operator.
      control.setValue(true, { emitEvent: false });
      control.enable({ emitEvent: false });
    }
  }

  /**
   * A field type with no query-index slot cannot participate in the duplicate-detection key however the
   * switch is set, so it is pinned off and disabled rather than left as a control with no effect - the
   * hint beside it says why. getRawValue still returns the (forced-false) value.
   */
  private applyUniqueKeyAvailability(fieldTypeName?: string): void {
    this.uniqueKeyUnsupported.set(
      !!fieldTypeName && this.indexableByFieldType.get(fieldTypeName) === false,
    );
    const control = this.form.controls.isUniqueKey;
    if (this.uniqueKeyUnsupported()) {
      control.setValue(false, { emitEvent: false });
      control.disable({ emitEvent: false });
    } else if (control.disabled) {
      // Restore the default now that the switch is meaningful again - it was pinned to false and
      // locked by a previous, non-indexable type selection, not by the operator.
      control.setValue(false, { emitEvent: false });
      control.enable({ emitEvent: false });
    }
  }

  /**
   * The "configuration" form group is injected dynamically by <ff-flex-field-config> (its shape depends
   * on whichever field type is currently selected), so it does not exist on this.form's own declared
   * shape.
   */
  private configurationValue(): Record<string, object> {
    return (this.form as FormGroup).get('configuration')?.value ?? {};
  }

  /**
   * Bridges a server-returned FieldDefinitionDto into the plain object <ff-flex-field-config> needs.
   *
   * Deliberately rebuilt field-by-field rather than cast: the DTO instance carries ABP's own
   * validation-tracking properties (ngx-validate decorates every HTTP-response object with per-field
   * `_name`/`_displayName`/... entries holding live RxJS subjects), and FlexFieldConfigComponent.render()
   * hands `selected` to structuredClone() before patching it into the editor's form - which throws on
   * those subjects (functions can never be cloned) and silently aborts the whole render, leaving the
   * config panel blank as if nothing were stored. A hand-built plain object carries none of that.
   */
  private toFieldData(field: FieldDefinitionDto): FlexFieldData {
    return {
      id: field.id ?? '',
      name: field.name ?? '',
      displayName: field.displayName ?? '',
      description: field.description ?? undefined,
      fieldTypeName: field.fieldTypeName ?? '',
      configuration: field.configuration ?? {},
    };
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
    this.router.navigate(['/vault-extract/document-types']);
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
    const fieldTypeName = this.fieldTypes()[0]?.name ?? '';
    this.form.reset({
      name: '',
      displayName: '',
      description: '',
      fieldTypeName,
      displayOrder: nextOrder,
      isRequired: false,
      isSearchable: true,
      isUniqueKey: false,
    });
    this.form.controls.name.enable();
    // reset() does not change a control's disabled status (Angular only does that for a per-control
    // {value, disabled} form-state object, not a plain value map) - so a switch left disabled by the
    // PREVIOUS modal instance's non-indexable type would otherwise survive into this one and make
    // applySearchableAvailability below misread it as "locked by this field's own type".
    this.form.controls.isSearchable.enable({ emitEvent: false });
    this.form.controls.isUniqueKey.enable({ emitEvent: false });
    this.configSeed.set(undefined);
    this.applySearchableAvailability(fieldTypeName);
    this.applyUniqueKeyAvailability(fieldTypeName);
    // Must be called after form.reset()/enable(): both trigger valueChanges that can be misread as
    // "manual edit". reset() clears that marker and resets suggestion state, including the spinner.
    this.slugHandle?.reset();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.isPolishing.set(false);
    this.showPromptPreview.set(false);
    this.editing.set('create');
  }

  openEdit(field: FieldDefinitionDto): void {
    // Disable before reset so slug auto-suggestion sees edit-mode reset as not automatically managed and
    // does not clear the existing name as a stale key. See wireSlugSuggestion comments.
    this.form.controls.name.disable();
    this.form.reset({
      name: field.name,
      displayName: field.displayName,
      description: field.description ?? '',
      fieldTypeName: field.fieldTypeName ?? '',
      displayOrder: field.displayOrder,
      isRequired: field.isRequired,
      isSearchable: field.isSearchable ?? true,
      isUniqueKey: field.isUniqueKey ?? false,
    });
    this.form.controls.name.enable();
    // Same reason as openCreate: reset() preserves whatever disabled status the control already had, so
    // a previous edit's non-indexable type would otherwise leave this disabled and make
    // applySearchableAvailability below stomp this field's own real IsSearchable back to true.
    this.form.controls.isSearchable.enable({ emitEvent: false });
    this.form.controls.isUniqueKey.enable({ emitEvent: false });
    // The stored field, not just its type: <ff-flex-field-config> only restores saved configuration
    // values when selected.fieldTypeName matches the type currently being rendered, so handing it the
    // whole field lets it patch what an admin previously set instead of quietly re-defaulting. Rebuilt
    // as a plain object (not the DTO instance itself) - see toFieldData.
    this.configSeed.set(this.toFieldData(field));
    this.applySearchableAvailability(field.fieldTypeName);
    this.applyUniqueKeyAvailability(field.fieldTypeName);
    this.slugHandle?.markManual();
    this.justDrafted.set(false);
    this.isDrafting.set(false);
    this.isPolishing.set(false);
    this.showPromptPreview.set(false);
    this.editing.set(field);
    // The field-type <select> is freshly mounted by the @if above, with fieldTypeName already set to a
    // non-first option: Angular writes that value into the native select before its own <option>s finish
    // registering (a select-with-preset-value-on-first-render gap, independent of @for vs *ngFor and
    // [value] vs [ngValue] - tried both), so the select silently displays the first option while the
    // control itself - and everything else reading it, like <ff-flex-field-config> - already holds the
    // real one. Re-push the same value once the view has settled (setTimeout, not queueMicrotask: this
    // needs to run after Angular's own view-creation microtasks, not just after this function's own
    // synchronous scope) to force a fresh sync. emitEvent:false: this is a display fixup, not a change -
    // it must not re-clear configSeed via the fieldTypeName.valueChanges subscription in ngOnInit.
    const fieldTypeName = field.fieldTypeName ?? '';
    setTimeout(() => this.form.controls.fieldTypeName.setValue(fieldTypeName, { emitEvent: false }));
  }

  // #264: draft field metadata from the prompt. The prompt is the primary input; one LLM call drafts the
  // remaining fields, applies them as a group, and lets the user review or modify each item.
  draft(): void {
    const prompt = (this.form.controls.description.value ?? '').trim();
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
          this.toaster.warn('VaultExtract::FieldDefinition:DraftUnavailable', 'VaultExtract::Warning');
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
      this.toaster.info('VaultExtract::FieldDefinition:DraftUnavailable', 'VaultExtract::Info');
      return;
    }
    const fieldTypeName = draft.fieldTypeName ?? this.fieldTypes()[0]?.name ?? '';
    this.form.controls.displayName.setValue(draft.displayName, { emitEvent: false });
    this.form.controls.fieldTypeName.setValue(fieldTypeName, { emitEvent: false });
    this.form.controls.isRequired.setValue(draft.isRequired ?? false, { emitEvent: false });
    // setValue used emitEvent:false, so the config panel would still describe the previous type unless
    // told directly. The server can draft a configuration alongside the type - a date-only DateTime, a
    // Tags MaxCount - so seed <ff-flex-field-config> with it via the same "selected" channel it uses to
    // restore a stored field's configuration.
    this.configSeed.set({
      id: '',
      name: '',
      displayName: draft.displayName,
      fieldTypeName,
      configuration: draft.configuration ?? {},
    });
    this.applySearchableAvailability(fieldTypeName);
    this.applyUniqueKeyAvailability(fieldTypeName);
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

  // #447: AI-polish the prompt TEXT itself (distinct from #264 draft, which infers the other metadata).
  // One LLM call rewrites the operator's raw instruction into clean Markdown and writes it back into the
  // editor for review. Fail-open: the backend returns the original prompt unchanged on provider failure,
  // and errors surface as a non-destructive toast — the operator's input is never lost.
  polish(): void {
    const prompt = (this.form.controls.description.value ?? '').trim();
    if (!prompt || this.isPolishing()) return;
    this.isPolishing.set(true);
    // `undefined` for the generated `cancellationToken` parameter, as `draftService.draft` and
    // `slugService.suggest` already do: the C# action declares a CancellationToken, so the ABP proxy
    // generator emits it positionally. It is never sent — ASP.NET Core supplies RequestAborted.
    this.polishService.polish({ prompt }, undefined)
      // Cancel when the modal closes so a late response cannot overwrite a reopened form (mirrors draft).
      .pipe(takeUntil(this.draftCancelled$), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.isPolishing.set(false);
          if (result.prompt && result.prompt !== prompt) {
            // emitEvent:false so the displayName→slug wiring is not disturbed.
            this.form.controls.description.setValue(result.prompt, { emitEvent: false });
            this.form.controls.description.markAsDirty();
            this.showPromptPreview.set(true);
            this.toaster.success('VaultExtract::FieldDefinition:PolishDone', 'VaultExtract::Success');
          } else {
            this.toaster.info('VaultExtract::FieldDefinition:PolishNoChange', 'VaultExtract::Info');
          }
        },
        error: () => {
          this.isPolishing.set(false);
          this.toaster.warn('VaultExtract::FieldDefinition:PolishUnavailable', 'VaultExtract::Warning');
        },
      });
  }

  // #447: render the current prompt as Markdown for the Edit/Preview toggle. Angular sanitizes the bound
  // [innerHTML], so marked's output is safe to render. Read straight from the control (not a valueChanges
  // signal) so polish()'s emitEvent:false write-back is reflected; re-parse only when the prompt changed.
  promptPreviewHtml(): string {
    const prompt = this.form.controls.description.value ?? '';
    if (prompt !== this.promptPreviewSource) {
      this.promptPreviewSource = prompt;
      this.promptPreviewCache = marked.parse(prompt, { gfm: true, async: false }) as string;
    }
    return this.promptPreviewCache;
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
    this.isPolishing.set(false);
    this.showPromptPreview.set(false);
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
    const configuration = this.configurationValue();

    if (mode === 'create') {
      const input: CreateFieldDefinitionDto = {
        documentTypeId: this.documentTypeId,
        name: raw.name,
        displayName: raw.displayName,
        description: raw.description,
        fieldTypeName: raw.fieldTypeName,
        configuration,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        // Disabled for a non-indexable type, but getRawValue still carries it back after the policy has
        // set it to false.
        isSearchable: raw.isSearchable,
        isUniqueKey: raw.isUniqueKey,
      };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('VaultExtract::FieldDefinition:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, {
        name: raw.name,
        displayName: raw.displayName,
        description: raw.description,
        fieldTypeName: raw.fieldTypeName,
        configuration,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        isSearchable: raw.isSearchable,
        isUniqueKey: raw.isUniqueKey,
      })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('VaultExtract::FieldDefinition:UpdatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    }
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, 'VaultExtract::Success');
    this.load();
  }

  delete(field: FieldDefinitionDto): void {
    this.confirmation
      .warn('VaultExtract::FieldDefinition:AreYouSureToDelete', 'VaultExtract::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(field.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('VaultExtract::FieldDefinition:DeletedSuccessfully', 'VaultExtract::Success');
              this.load();
            },
            error: () => this.toaster.error('VaultExtract::FieldDefinition:DeleteFailed', 'VaultExtract::Error'),
          });
      });
  }

  restore(field: FieldDefinitionDto): void {
    this.service.restore(field.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.toaster.success('VaultExtract::FieldDefinition:RestoredSuccessfully', 'VaultExtract::Success');
          this.load();
        },
      });
  }
}

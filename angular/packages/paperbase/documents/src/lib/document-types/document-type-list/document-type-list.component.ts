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
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { map } from 'rxjs';
import {
  CreateDocumentTypeDto,
  DocumentTypeDto,
  DocumentTypeService,
  PAPERBASE_PERMISSIONS,
  SlugSuggestionService,
} from '@dignite/paperbase';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';

// Mirrors DocumentTypeConsts (Domain.Shared): TypeCode whitelist + length cap.
const TYPE_CODE_PATTERN = /^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$/;
const MAX_TYPE_CODE_LENGTH = 128;
const MAX_DISPLAY_NAME_LENGTH = 128;

@Component({
  selector: 'lib-document-type-list',
  templateUrl: './document-type-list.component.html',
  styleUrls: ['./document-type-list.component.scss'],
  imports: [CommonModule, RouterModule, ReactiveFormsModule, LocalizationPipe],
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

  // Both app services are guarded by Documents.ConfirmClassification on the backend.
  readonly canManage = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );

  types = signal<DocumentTypeDto[]>([]);
  isLoading = signal(true);
  showDeleted = signal(false);

  // null = closed; 'create' / DocumentTypeDto = open in the matching mode.
  editing = signal<DocumentTypeDto | 'create' | null>(null);
  isSubmitting = signal(false);
  isSuggesting = signal(false);

  private slugHandle?: SlugSuggestionHandle;

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
    confidenceThreshold: [0.7, [Validators.required, Validators.min(0), Validators.max(1)]],
    priority: [0, [Validators.required]],
  });

  ngOnInit(): void {
    this.slugHandle = wireSlugSuggestion({
      displayName: this.form.controls.displayName,
      target: this.form.controls.typeCode,
      suggest: text => this.slugService.suggest({ displayName: text }).pipe(map(r => r.slug)),
      fallback: () => this.nextTypeCode(),
      destroyRef: this.destroyRef,
      onPending: pending => this.isSuggesting.set(pending),
    });
    this.load();
  }

  // LLM 不可用 / 未翻译时的本地回退：取与现有类型代码不冲突的最小 type_{n}。
  private nextTypeCode(): string {
    const existing = new Set(this.types().map(t => t.typeCode));
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
        this.types.set(list);
        this.isLoading.set(false);
      },
      error: () => this.isLoading.set(false),
    });
  }

  openCreate(): void {
    this.form.reset({ typeCode: '', displayName: '', confidenceThreshold: 0.7, priority: 0 });
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
      confidenceThreshold: type.confidenceThreshold,
      priority: type.priority,
    });
    this.form.controls.typeCode.enable();
    this.slugHandle?.markManual();
    this.editing.set(type);
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
      this.service.update(mode.id, {
        typeCode: raw.typeCode,
        displayName: raw.displayName,
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
        this.service.delete(type.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::DocumentType:DeletedSuccessfully', '::Success');
              this.load();
            },
          });
      });
  }

  restore(type: DocumentTypeDto): void {
    this.service.restore(type.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.toaster.success('::DocumentType:RestoredSuccessfully', '::Success');
          this.load();
        },
      });
  }

  manageFields(type: DocumentTypeDto): void {
    this.router.navigate(['/documents/types', type.typeCode, 'fields']);
  }
}

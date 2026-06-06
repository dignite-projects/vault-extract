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
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  CabinetDto,
  CabinetService,
  CreateCabinetDto,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';

// Mirrors CabinetConsts (Domain.Shared): Name / Description length caps.
const MAX_NAME_LENGTH = 128;
const MAX_DESCRIPTION_LENGTH = 512;

@Component({
  selector: 'lib-cabinet-list',
  templateUrl: './cabinet-list.component.html',
  styleUrls: ['./cabinet-list.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CabinetListComponent implements OnInit {
  private readonly service = inject(CabinetService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canCreate = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Create);
  readonly canUpdate = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Update);
  readonly canDelete = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Delete);

  cabinets = signal<CabinetDto[]>([]);
  isLoading = signal(true);

  // null = closed; 'create' / CabinetDto = open in the matching mode.
  editing = signal<CabinetDto | 'create' | null>(null);
  isSubmitting = signal(false);

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(MAX_NAME_LENGTH)]],
    description: ['', [Validators.maxLength(MAX_DESCRIPTION_LENGTH)]],
  });

  ngOnInit(): void {
    this.load();
  }

  refresh(): void {
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.service.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.cabinets.set(list);
          this.isLoading.set(false);
        },
        error: () => this.isLoading.set(false),
      });
  }

  openCreate(): void {
    this.form.reset({ name: '', description: '' });
    this.editing.set('create');
  }

  openEdit(cabinet: CabinetDto): void {
    this.form.reset({ name: cabinet.name, description: cabinet.description ?? '' });
    this.editing.set(cabinet);
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
    // 空白描述归一化为 undefined——后端 ValidateDescription 同样把空白视作"无说明"。
    const description = raw.description.trim() || undefined;

    if (mode === 'create') {
      const input: CreateCabinetDto = { name: raw.name, description };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::Cabinet:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, { name: raw.name, description })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::Cabinet:UpdatedSuccessfully'),
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

  delete(cabinet: CabinetDto): void {
    this.confirmation
      .warn('::Cabinet:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(cabinet.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Cabinet:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::Cabinet:DeleteFailed', '::Error'),
          });
      });
  }
}
